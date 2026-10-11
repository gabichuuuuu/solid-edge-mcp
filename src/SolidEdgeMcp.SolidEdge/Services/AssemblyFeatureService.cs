using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;
using Faces = SolidEdgeGeometry.Faces;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Assembly features: extruded cutouts made in the assembly through some of its parts, from a closed sketch on an assembly
/// reference plane (se_sketch create in the assembly). The part files do not change. Each cut is checked by the faces it
/// leaves on every part it goes through; verified: a hole of radius 5 through a 10 mm plate leaves one cylindrical face of
/// 2π·5·10 mm². With the profile on the base plane under the plate, igRight (along the plane normal) cuts and igLeft fails.
/// </summary>
public static class AssemblyFeatureService
{
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object[] features = Cutouts(assembly).Select(f => Describe(context, assembly, f)).ToArray();
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), count = features.Length, features };
    }

    /// <summary>
    /// Extruded cutout through <paramref name="occurrenceHandles"/>, through all or to a depth (mm), on the side given or, with
    /// "auto", along the plane normal and then against it.
    /// </summary>
    public static object Cut(SeContext context, string? sketch, string[]? occurrenceHandles, double? depth, string side, string? name)
    {
        if (occurrenceHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'occurrences' must list the parts to cut (occurrence handles from se_assembly list).");
        }

        Occurrence[] scope = occurrenceHandles.Select(h => Occurrence(context, h, "occurrences")).ToArray();
        if (scope.FirstOrDefault(o => o.Subassembly) is { } sub)
        {
            throw ToolException.InvalidArgument($"'{sub.Name}' is a subassembly: list the parts to cut.");
        }

        Profile profile = SketchService.Resolve(context, sketch ?? throw ToolException.InvalidArgument(
            "'sketch' is required: a closed sketch made in the assembly (se_sketch create with an assembly plane, then close)."));
        double distance = depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : 0;
        FeaturePropertyConstants[] sides = side switch
        {
            "auto" => [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
            "normal" => [FeaturePropertyConstants.igRight],
            "reverse" => [FeaturePropertyConstants.igLeft],
            "symmetric" => [FeaturePropertyConstants.igSymmetric],
            _ => throw ToolException.InvalidArgument($"Unknown side '{side}'.", "Use auto, normal, reverse or symmetric."),
        };

        AssemblyDocument assembly = scope[0].TopLevelDocument;
        if (name is not null && Find(assembly, name) is not null)
        {
            throw ToolException.InvalidArgument($"The assembly already has a feature named '{name}'.", "Pass another 'name'.");
        }

        AssemblyFeaturesExtrudedCutouts cutouts = assembly.AssemblyFeatures.AssemblyFeaturesExtrudedCutouts;
        string? problem = null;
        foreach (FeaturePropertyConstants extent in sides)
        {
            Array parts = scope.Cast<object>().ToArray();
            Array profiles = new object[] { profile };
            FeaturePropertyConstants extentSide = extent;
            double length = distance;
            KeyPointExtentConstants flags = 0;
            AssemblyFeaturesExtrudedCutout cut;
            try
            {
                cut = cutouts.Add(parts.Length, ref parts, 1, ref profiles,
                    depth is null ? FeaturePropertyConstants.igThroughAll : FeaturePropertyConstants.igFinite,
                    ref extentSide, FeaturePropertyConstants.igLeft, ref length, null, ref flags, null, null);
            }
            catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
            {
                problem = ex.Message.Trim();
                continue;
            }

            // Each part in the scope must keep faces of the cut; otherwise the cut is removed and the next side is tried.
            string? status = FeatureService.StatusOf(cut);
            Occurrence? untouched = scope.FirstOrDefault(o => FacesOn(cut, o).Length == 0);
            if (status is "ok" or "warning" && untouched is null)
            {
                if (name is not null)
                {
                    cut.Name = name;
                }

                Hide(profile);
                object described = Describe(context, assembly, cut);
                return status == "ok" ? described : new { feature = described, warnings = new[] { "Solid Edge reports a warning on this feature." } };
            }

            problem = untouched is null ? $"its status is {status}" : $"it does not reach '{untouched.Name}'";
            TryDelete(cut);
        }

        throw new ToolException("feature_failed", $"Solid Edge did not cut the parts ({problem}) and the cut was removed.",
            "Check that the sketch is closed and that the parts are in its path, or give another side or a depth.");
    }

    /// <summary>Deletes an assembly feature by name; the parts get back the material it removed.</summary>
    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        AssemblyFeaturesExtrudedCutout cut = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the feature name (action 'list').")
            : Find(assembly, name) ?? throw ToolException.InvalidArgument($"The assembly has no feature '{name}'.", "List them with action 'list'.");
        cut.Delete();
        return Find(assembly, name) is null
            ? new { deleted = name, count = Cutouts(assembly).Count() }
            : throw new ToolException("feature_failed", $"Solid Edge did not delete the feature '{name}'.", "Check it with action 'list'.");
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, AssemblyFeaturesExtrudedCutout cut)
    {
        Array parts = Array.Empty<object>();
        cut.GetScopeParts(out _, out parts);
        FeaturePropertyConstants extent = cut.GetExtentType();
        return new
        {
            name = cut.Name,
            type = "extruded_cutout",
            status = FeatureService.StatusOf(cut),
            extent = extent == FeaturePropertyConstants.igFinite ? "finite" : extent == FeaturePropertyConstants.igThroughAll ? "through_all" : extent.ToString(),
            depth = extent == FeaturePropertyConstants.igFinite ? Geo.Mm(cut.GetDepth()) : (double?)null,
            side = cut.GetExtentSide() switch
            {
                FeaturePropertyConstants.igRight => "normal",
                FeaturePropertyConstants.igLeft => "reverse",
                FeaturePropertyConstants.igSymmetric => "symmetric",
                var other => other.ToString(),
            },
            parts = parts.OfType<Occurrence>().Select(o =>
            {
                SolidEdgeGeometry.Face[] faces = FacesOn(cut, o);
                return new
                {
                    occurrence = context.Register(o, "occurrence", (SolidEdgeDocument)assembly),
                    name = o.Name,
                    faces = faces.Length,
                    area = Geo.Mm2(faces.Sum(f => f.Area)),
                };
            }).ToArray(),
        };
    }

    /// <summary>
    /// Faces the cut leaves on one part of its scope; none if it misses the part (verified: Solid Edge then answers E_FAIL).
    /// </summary>
    private static SolidEdgeGeometry.Face[] FacesOn(AssemblyFeaturesExtrudedCutout cut, Occurrence occurrence)
    {
        try
        {
            return [.. Late.Items((Faces)cut.FacesForScopePart[occurrence, TopologyQuery.igQueryAll]).OfType<SolidEdgeGeometry.Face>()];
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return [];
        }
    }

    private static IEnumerable<AssemblyFeaturesExtrudedCutout> Cutouts(AssemblyDocument assembly) =>
        Items<AssemblyFeaturesExtrudedCutout>(assembly.AssemblyFeatures.AssemblyFeaturesExtrudedCutouts);

    private static AssemblyFeaturesExtrudedCutout? Find(AssemblyDocument assembly, string name) =>
        Cutouts(assembly).FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static void Hide(Profile profile)
    {
        try
        {
            profile.Visible = false;
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // Affects only the presentation; the feature is already created and verified.
        }
    }

    private static void TryDelete(AssemblyFeaturesExtrudedCutout cut)
    {
        try
        {
            cut.Delete();
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            // The error being reported is the original one.
        }
    }
}
