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
/// Assembly features: extruded and revolved cutouts and holes made in the assembly through some of its parts, from a sketch on
/// an assembly reference plane (se_sketch create in the assembly) or, for holes, from centers on a plane. The part files do not
/// change. Each feature is checked by the faces it leaves on every part it goes through; verified: a hole of radius 5 through
/// a 10 mm plate leaves one cylindrical face of 2π·5·10 mm². With the profile on the base plane under the plate, igRight
/// (along the plane normal) cuts and igLeft fails. A revolved cutout fails when its profile only removes material that is
/// already gone (e.g. inside an existing hole). Assembly holes have no IDispatch: they are handled through their typed interface.
/// </summary>
public static class AssemblyFeatureService
{
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object[] features = Features(assembly).Select(f => Describe(context, assembly, f.Feature, f.Type)).ToArray();
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), count = features.Length, features };
    }

    /// <summary>
    /// Extruded cutout through <paramref name="occurrenceHandles"/>, through all or to a depth (mm), on the side given or, with
    /// "auto", along the plane normal and then against it.
    /// </summary>
    public static object Cut(SeContext context, string? sketch, string[]? occurrenceHandles, double? depth, string side, string? name)
    {
        Occurrence[] scope = Scope(context, occurrenceHandles, "cut");
        Profile profile = SketchService.Resolve(context, sketch ?? throw ToolException.InvalidArgument(
            "'sketch' is required: a closed sketch made in the assembly (se_sketch create with an assembly plane, then close)."));
        double distance = depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : 0;
        FeaturePropertyConstants[] sides = Sides(side, true);
        AssemblyDocument assembly = scope[0].TopLevelDocument;
        CheckName(assembly, name);
        AssemblyFeaturesExtrudedCutouts cutouts = assembly.AssemblyFeatures.AssemblyFeaturesExtrudedCutouts;
        object result = Make(context, assembly, scope, sides, name, "extruded_cutout", extent =>
        {
            Array parts = scope.Cast<object>().ToArray();
            Array profiles = new object[] { profile };
            FeaturePropertyConstants extentSide = extent;
            double length = distance;
            KeyPointExtentConstants flags = 0;
            return cutouts.Add(parts.Length, ref parts, 1, ref profiles,
                depth is null ? FeaturePropertyConstants.igThroughAll : FeaturePropertyConstants.igFinite,
                ref extentSide, FeaturePropertyConstants.igLeft, ref length, null, ref flags, null, null);
        }, "Check that the sketch is closed and that the parts are in its path, or give another side or a depth.");
        Hide(profile);
        return result;
    }

    /// <summary>
    /// Revolved cutout through <paramref name="occurrenceHandles"/>: a closed sketch made in the assembly turned about its axis
    /// (se_sketch 'axis' before 'close') by an angle in degrees (360 by default), on the side given or, with "auto", both.
    /// </summary>
    public static object Revolve(SeContext context, string? sketch, string? axis, string[]? occurrenceHandles, double? angle, string side, string? name)
    {
        Occurrence[] scope = Scope(context, occurrenceHandles, "cut");
        Profile profile = SketchService.Resolve(context, sketch ?? throw ToolException.InvalidArgument(
            "'sketch' is required: a closed sketch made in the assembly, with an axis (se_sketch 'axis' on a line, before 'close')."));
        object refAxis = context.Handles.Resolve(axis ?? throw ToolException.InvalidArgument(
            "'axis' is required: the axis returned by se_sketch 'axis' on a line of the sketch, before 'close'."), "axis");
        double degrees = angle ?? 360;
        if (degrees is <= 0 or > 360)
        {
            throw ToolException.InvalidArgument("'angle' must be greater than 0 and at most 360 degrees.");
        }

        FeaturePropertyConstants[] sides = Sides(side, true);
        AssemblyDocument assembly = scope[0].TopLevelDocument;
        CheckName(assembly, name);
        AssemblyFeaturesRevolvedCutouts revolved = assembly.AssemblyFeatures.AssemblyFeaturesRevolvedCutouts;
        object result = Make(context, assembly, scope, sides, name, "revolved_cutout", extent =>
        {
            Array parts = scope.Cast<object>().ToArray();
            Array profiles = new object[] { profile };
            double radians = Units.DegToRad(degrees);
            KeyPointExtentConstants flags = 0;
            return revolved.Add(parts.Length, ref parts, 1, ref profiles, refAxis, FeaturePropertyConstants.igFinite, extent,
                FeaturePropertyConstants.igLeft, ref radians, null, ref flags, null, null);
        }, "Check that the sketch is closed, does not cross the axis and removes material of every part (not only inside existing holes).");
        Hide(profile);
        return result;
    }

    /// <summary>
    /// Holes through <paramref name="occurrenceHandles"/> at centers (assembly mm) projected onto an assembly plane: simple or
    /// countersink, through all or to a depth, toward the plane normal or against it ("auto" tries both). Each center gets its own
    /// profile (with several hole points in one profile only the first is drilled). Verified: a counterbore assembly hole comes
    /// out as a through hole of the counterbore diameter, so it is refused.
    /// </summary>
    public static object Hole(SeContext context, string? documentHandle, string[]? occurrenceHandles, HoleSpec spec, string? name)
    {
        Occurrence[] scope = Scope(context, occurrenceHandles, "drill");
        if (string.IsNullOrWhiteSpace(spec.Plane))
        {
            throw ToolException.InvalidArgument("'plane' is required: an assembly plane '1'-'3' or an assembly reference plane handle.");
        }

        List<(double X, double Y, double Z)> centers = Geo.Points3(spec.Centers, "centers", 1);
        if (spec.Type is "tapped" or "counterbore")
        {
            throw ToolException.InvalidArgument("Assembly holes are simple or countersink: Solid Edge makes a counterbore assembly hole as a through hole of the counterbore diameter.",
                "Make tapped and counterbore holes in the part (se_hole).");
        }

        FeaturePropertyConstants holeType = FeatureService.HoleTypeOf(spec);
        FeaturePropertyConstants extentType = spec.Extent switch
        {
            "finite" => FeaturePropertyConstants.igFinite,
            "through_all" => FeaturePropertyConstants.igThroughAll,
            _ => throw ToolException.InvalidArgument($"Unknown extent '{spec.Extent}'.", "Use finite or through_all."),
        };
        double depth = extentType == FeaturePropertyConstants.igFinite
            ? Units.MmToM(Units.RequirePositive(spec.Depth ?? throw ToolException.InvalidArgument("Extent 'finite' requires 'depth' (mm)."), "depth"))
            : 0;
        FeaturePropertyConstants[] sides = Sides(spec.Direction, false);
        AssemblyDocument assembly = documentHandle is null ? scope[0].TopLevelDocument : Assembly(context, documentHandle);
        CheckName(assembly, name);
        HoleData data = FeatureService.CreateHoleData(assembly.HoleDataCollection, spec, holeType, depth);
        object refPlane = SketchService.AssemblyPlane(context, assembly, spec.Plane);
        var sets = new List<ProfileSet>();
        var profiles = new List<Profile>();
        foreach ((double x, double y, double z) in centers)
        {
            ProfileSet set = assembly.ProfileSets.Add();
            sets.Add(set);
            Profile profile = set.Profiles.Add(refPlane);
            profile.Convert3DCoordinate(x, y, z, out double u, out double v);
            profile.Holes2d.Add(u, v);
            profile.End(ProfileValidationType.igProfileClosed);
            profiles.Add(profile);
        }

        AssemblyFeaturesHoles holes = assembly.AssemblyFeatures.AssemblyFeaturesHoles;
        try
        {
            object result = Make(context, assembly, scope, sides, name, "hole", extent =>
            {
                Array parts = scope.Cast<object>().ToArray();
                Array profileArray = profiles.Cast<object>().ToArray();
                FeaturePropertyConstants extentSide = extent;
                double length = depth;
                KeyPointExtentConstants flags = 0;
                return holes.Add((uint)parts.Length, ref parts, (uint)profileArray.Length, ref profileArray, ref extentSide, data, extentType, ref length,
                    null, null, null, ref flags);
            }, "Check that the centers are over the parts, or give the other direction or a larger depth.");
            profiles.ForEach(Hide);
            return result;
        }
        catch (ToolException)
        {
            sets.ForEach(TryDelete);
            throw;
        }
    }

    /// <summary>Deletes an assembly feature by name; the parts get back the material it removed.</summary>
    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object feature = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the feature name (action 'list').")
            : Find(assembly, name) ?? throw ToolException.InvalidArgument($"The assembly has no feature '{name}'.", "List them with action 'list'.");
        DeleteFeature(feature);
        return Find(assembly, name) is null
            ? new { deleted = name, count = Features(assembly).Count() }
            : throw new ToolException("feature_failed", $"Solid Edge did not delete the feature '{name}'.", "Check it with action 'list'.");
    }

    private static Occurrence[] Scope(SeContext context, string[]? occurrenceHandles, string verb)
    {
        if (occurrenceHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"'occurrences' must list the parts to {verb} (occurrence handles from se_assembly list).");
        }

        Occurrence[] scope = occurrenceHandles.Select(h => Occurrence(context, h, "occurrences")).ToArray();
        return scope.FirstOrDefault(o => o.Subassembly) is { } sub
            ? throw ToolException.InvalidArgument($"'{sub.Name}' is a subassembly: list the parts to {verb}.")
            : scope;
    }

    private static FeaturePropertyConstants[] Sides(string side, bool symmetric) => side switch
    {
        "auto" => [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
        "normal" => [FeaturePropertyConstants.igRight],
        "reverse" => [FeaturePropertyConstants.igLeft],
        "symmetric" when symmetric => [FeaturePropertyConstants.igSymmetric],
        _ => throw ToolException.InvalidArgument($"Unknown side '{side}'.", symmetric ? "Use auto, normal, reverse or symmetric." : "Use auto, normal or reverse."),
    };

    private static void CheckName(AssemblyDocument assembly, string? name)
    {
        if (name is not null && Find(assembly, name) is not null)
        {
            throw ToolException.InvalidArgument($"The assembly already has a feature named '{name}'.", "Pass another 'name'.");
        }
    }

    /// <summary>
    /// Creates the feature on each side in turn until it is fine and leaves faces on every part of the scope; a failed attempt is
    /// deleted before the next one.
    /// </summary>
    private static object Make(SeContext context, AssemblyDocument assembly, Occurrence[] scope, FeaturePropertyConstants[] sides,
        string? name, string type, Func<FeaturePropertyConstants, object> add, string hint)
    {
        string? problem = null;
        foreach (FeaturePropertyConstants extent in sides)
        {
            object feature;
            try
            {
                feature = add(extent);
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsDisconnected(ex))
            {
                problem = ComErrors.Unwrap(ex).Message.Trim();
                continue;
            }

            string? status = StatusOf(feature);
            Occurrence? untouched = scope.FirstOrDefault(o => FacesOn(feature, o).Length == 0);
            if (status is "ok" or "warning" && untouched is null)
            {
                if (name is not null)
                {
                    if (feature is AssemblyFeaturesHole hole)
                    {
                        hole.Name = name;
                    }
                    else
                    {
                        Late.Set(feature, "Name", name);
                    }
                }

                object described = Describe(context, assembly, feature, type);
                return status == "ok" ? described : new { feature = described, warnings = new[] { "Solid Edge reports a warning on this feature." } };
            }

            problem = untouched is null ? $"its status is {status}" : $"it does not reach '{untouched.Name}'";
            TryDeleteFeature(feature);
        }

        throw new ToolException("feature_failed", $"Solid Edge did not make the {type.Replace('_', ' ')} in the parts ({problem}) and it was removed.", hint);
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, object feature, string type)
    {
        Array parts = Array.Empty<object>();
        FeaturePropertyConstants extent;
        FeaturePropertyConstants side;
        double? depth = null;
        double? angle = null;
        switch (feature)
        {
            case AssemblyFeaturesExtrudedCutout cut:
                cut.GetScopeParts(out _, out parts);
                extent = cut.GetExtentType();
                side = cut.GetExtentSide();
                depth = extent == FeaturePropertyConstants.igFinite ? Geo.Mm(cut.GetDepth()) : null;
                break;
            case AssemblyFeaturesRevolvedCutout revolved:
                revolved.GetScopeParts(out _, out parts);
                extent = revolved.GetExtentType();
                side = revolved.GetExtentSide();
                // A full turn is read back as 0.
                double turned = Math.Round(Units.RadToDeg(revolved.GetAngle()), 6);
                angle = turned < 1e-9 ? 360 : turned;
                break;
            case AssemblyFeaturesHole hole:
                hole.GetScopeParts(out _, out parts);
                extent = hole.GetExtentType();
                side = hole.GetExtentSide();
                depth = extent == FeaturePropertyConstants.igFinite ? Geo.Mm(hole.GetDepth()) : null;
                break;
            default:
                throw new InvalidOperationException($"Unexpected assembly feature {feature.GetType().Name}.");
        }

        return new
        {
            name = NameOf(feature),
            type,
            status = StatusOf(feature),
            extent = extent switch
            {
                FeaturePropertyConstants.igFinite => "finite",
                FeaturePropertyConstants.igThroughAll => "through_all",
                var other => other.ToString(),
            },
            depth,
            angle,
            side = side switch
            {
                FeaturePropertyConstants.igRight => "normal",
                FeaturePropertyConstants.igLeft => "reverse",
                FeaturePropertyConstants.igSymmetric => "symmetric",
                var other => other.ToString(),
            },
            parts = parts.OfType<Occurrence>().Select(o =>
            {
                SolidEdgeGeometry.Face[] faces = FacesOn(feature, o);
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
    /// Faces the feature leaves on one part of its scope; none if it misses the part (verified: Solid Edge then answers E_FAIL).
    /// </summary>
    private static SolidEdgeGeometry.Face[] FacesOn(object feature, Occurrence occurrence)
    {
        try
        {
            object faces = feature switch
            {
                AssemblyFeaturesExtrudedCutout cut => cut.FacesForScopePart[occurrence, TopologyQuery.igQueryAll],
                AssemblyFeaturesRevolvedCutout revolved => revolved.FacesForScopePart[occurrence, TopologyQuery.igQueryAll],
                AssemblyFeaturesHole hole => hole.FacesForScopePart[occurrence, TopologyQuery.igQueryAll],
                _ => throw new InvalidOperationException($"Unexpected assembly feature {feature.GetType().Name}."),
            };
            return [.. Late.Items((Faces)faces).OfType<SolidEdgeGeometry.Face>()];
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
        {
            return [];
        }
    }

    private static IEnumerable<(object Feature, string Type)> Features(AssemblyDocument assembly)
    {
        AssemblyFeatures features = assembly.AssemblyFeatures;
        return Items<AssemblyFeaturesExtrudedCutout>(features.AssemblyFeaturesExtrudedCutouts).Select(f => ((object)f, "extruded_cutout"))
            .Concat(Items<AssemblyFeaturesRevolvedCutout>(features.AssemblyFeaturesRevolvedCutouts).Select(f => ((object)f, "revolved_cutout")))
            .Concat(Items<AssemblyFeaturesHole>(features.AssemblyFeaturesHoles).Select(f => ((object)f, "hole")));
    }

    private static object? Find(AssemblyDocument assembly, string name) =>
        Features(assembly).Select(f => f.Feature).FirstOrDefault(f => string.Equals(NameOf(f), name, StringComparison.OrdinalIgnoreCase));

    private static string? NameOf(object feature) => feature is AssemblyFeaturesHole hole ? hole.Name : Late.TryGetString(feature, "Name");

    private static string? StatusOf(object feature) =>
        feature is AssemblyFeaturesHole hole ? FeatureService.StatusName((int)hole.get_Status(out _)) : FeatureService.StatusOf(feature);

    private static void DeleteFeature(object feature)
    {
        if (feature is AssemblyFeaturesHole hole)
        {
            hole.Delete();
        }
        else
        {
            Late.Call(feature, "Delete");
        }
    }

    private static void TryDeleteFeature(object feature)
    {
        try
        {
            DeleteFeature(feature);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The error being reported is the original one.
        }
    }

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

    private static void TryDelete(object item)
    {
        try
        {
            Late.Call(item, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The error being reported is the original one.
        }
    }
}
