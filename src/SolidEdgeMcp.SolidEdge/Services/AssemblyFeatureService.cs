// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;
using Faces = SolidEdgeGeometry.Faces;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Assembly features: extruded and revolved cutouts, holes and mirrors of cutouts made in the assembly through some of its parts (protrusions,
/// patterns and welds: AssemblyFeatureService.Bodies.cs), from a sketch on an assembly reference plane (se_sketch create in the assembly) or, for holes, from centers on a plane. The part files do not
/// change. Each feature is checked by the faces it leaves on every part it goes through; verified: a hole of radius 5 through
/// a 10 mm plate leaves one cylindrical face of 2π·5·10 mm². With the profile on the base plane under the plate, igRight
/// (along the plane normal) cuts and igLeft fails. A revolved cutout fails when its profile only removes material that is
/// already gone (e.g. inside an existing hole). Assembly holes have no IDispatch: they are handled through their typed interface.
/// </summary>
public static partial class AssemblyFeatureService
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
    public static object Cut(SeContext context, string? sketch, string[]? occurrenceHandles, double? depth, PartFeatureSide side, string? name)
    {
        Occurrence[] scope = Scope(context, occurrenceHandles, "cut");
        Profile profile = SketchService.ResolveDefined(context, sketch ?? throw ToolException.InvalidArgument(
            "'sketch' is required: a closed sketch made in the assembly (se_sketch create with an assembly plane, then close)."));
        double distance = depth is { } d ? Units.MmToM(Units.RequirePositive(d, "depth")) : 0;
        FeaturePropertyConstants[] sides = Sides(ArgumentNames.Snake(side), true);
        AssemblyDocument assembly = scope[0].TopLevelDocument;
        CheckName(assembly, name);
        CheckUnused(assembly, profile);
        AssemblyFeaturesExtrudedCutouts cutouts = FeaturesOf(assembly).AssemblyFeaturesExtrudedCutouts;
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
        }, "Draw the sketch again, closed and with the parts in its path, or give another side or a depth.");
        Hide(profile);
        return result;
    }

    /// <summary>
    /// Revolved cutout through <paramref name="occurrenceHandles"/>: a closed sketch made in the assembly turned about its axis
    /// (se_sketch 'axis' before 'close') by an angle in degrees (360 by default), on the side given or, with "auto", both.
    /// </summary>
    public static object Revolve(SeContext context, string? sketch, string? axis, string[]? occurrenceHandles, double? angle, PartFeatureSide side, string? name)
    {
        Occurrence[] scope = Scope(context, occurrenceHandles, "cut");
        Profile profile = SketchService.ResolveDefined(context, sketch ?? throw ToolException.InvalidArgument(
            "'sketch' is required: a closed sketch made in the assembly, with an axis (se_sketch 'axis' on a line, before 'close')."));
        object refAxis = context.Handles.Resolve(axis ?? throw ToolException.InvalidArgument(
            "'axis' is required: the axis returned by se_sketch 'axis' on a line of the sketch, before 'close'."), "axis");
        double degrees = angle ?? 360;
        if (degrees is <= 0 or > 360)
        {
            throw ToolException.InvalidArgument("'angle' must be greater than 0 and at most 360 degrees.");
        }

        FeaturePropertyConstants[] sides = Sides(ArgumentNames.Snake(side), true);
        AssemblyDocument assembly = scope[0].TopLevelDocument;
        CheckName(assembly, name);
        CheckUnused(assembly, profile);
        AssemblyFeaturesRevolvedCutouts revolved = FeaturesOf(assembly).AssemblyFeaturesRevolvedCutouts;
        object result = Make(context, assembly, scope, sides, name, "revolved_cutout", extent =>
        {
            Array parts = scope.Cast<object>().ToArray();
            Array profiles = new object[] { profile };
            double radians = Units.DegToRad(degrees);
            KeyPointExtentConstants flags = 0;
            return revolved.Add(parts.Length, ref parts, 1, ref profiles, refAxis, FeaturePropertyConstants.igFinite, extent,
                FeaturePropertyConstants.igLeft, ref radians, null, ref flags, null, null);
        }, "Draw the sketch again, closed, not crossing the axis and removing material of every part (not only inside existing holes).");
        Hide(profile);
        return result;
    }

    /// <summary>
    /// Holes through <paramref name="occurrenceHandles"/> at centers (assembly mm) projected onto an assembly plane: simple or
    /// countersink, through all or to a depth, toward the plane normal or against it ("auto" tries both). Each center gets its own
    /// profile (with several hole points in one profile only the first is drilled). Verified: a counterbore assembly hole comes
    /// out as a through hole of the counterbore diameter, so a counterbore is made as a simple hole plus an extruded cutout of
    /// the counterbore diameter and depth from the same plane, on the side the hole went.
    /// </summary>
    public static object Hole(SeContext context, string? documentHandle, string[]? occurrenceHandles, HoleSpec spec, string? name)
    {
        Occurrence[] scope = Scope(context, occurrenceHandles, "drill");
        if (string.IsNullOrWhiteSpace(spec.Plane))
        {
            throw ToolException.InvalidArgument("'plane' is required: an assembly plane '1'-'3' or an assembly reference plane handle.");
        }

        List<(double X, double Y, double Z)> centers = Geo.Points3(spec.Centers, "centers", 1);
        if (spec.Type is HoleType.Tapped)
        {
            throw ToolException.InvalidArgument("Assembly holes are simple, countersink or counterbore: a tapped hole is made in the part.",
                "Make tapped holes in the part (se_hole) or thread them with se_thread.");
        }

        bool counterbore = spec.Type == HoleType.Counterbore;
        (double Diameter, double Depth) bore = default;
        if (counterbore)
        {
            double hole = Units.RequirePositive(spec.Diameter ?? throw ToolException.InvalidArgument("'diameter' is required (mm)."), "diameter");
            bore = (Units.RequirePositive(spec.CounterboreDiameter ?? throw ToolException.InvalidArgument("A counterbore hole needs 'counterboreDiameter' (mm)."), "counterboreDiameter"),
                Units.RequirePositive(spec.CounterboreDepth ?? throw ToolException.InvalidArgument("A counterbore hole needs 'counterboreDepth' (mm)."), "counterboreDepth"));
            if (bore.Diameter <= hole)
            {
                throw ToolException.InvalidArgument("'counterboreDiameter' must be larger than the hole 'diameter'.");
            }

            if (spec.Extent == SimpleExtent.Finite && spec.Depth is { } holeDepth && bore.Depth >= holeDepth)
            {
                throw ToolException.InvalidArgument("'counterboreDepth' must be smaller than the hole 'depth'.");
            }

            spec = spec with { Type = HoleType.Simple };
        }

        FeaturePropertyConstants holeType = FeatureService.HoleTypeOf(spec);
        FeaturePropertyConstants extentType = spec.Extent switch
        {
            SimpleExtent.Finite => FeaturePropertyConstants.igFinite,
            SimpleExtent.ThroughAll => FeaturePropertyConstants.igThroughAll,
            _ => throw ToolException.InvalidArgument($"Unknown extent '{ArgumentNames.Snake(spec.Extent)}'.", "Use finite or through_all."),
        };
        double depth = extentType == FeaturePropertyConstants.igFinite
            ? Units.MmToM(Units.RequirePositive(spec.Depth ?? throw ToolException.InvalidArgument("Extent 'finite' requires 'depth' (mm)."), "depth"))
            : 0;
        FeaturePropertyConstants[] sides = Sides(ArgumentNames.Snake(spec.Direction), false);
        AssemblyDocument assembly = documentHandle is null ? scope[0].TopLevelDocument : Assembly(context, documentHandle);
        CheckName(assembly, name);
        string? boreName = counterbore && name is not null ? $"{name} counterbore" : null;
        CheckName(assembly, boreName);
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
            try
            {
                SketchService.Define(profile);
            }
            catch (ToolException)
            {
                sets.ForEach(Late.TryDelete);
                throw;
            }
        }

        AssemblyFeaturesHoles holes = FeaturesOf(assembly).AssemblyFeaturesHoles;
        AssemblyFeaturesHole? made = null;
        bool holeMade = false;
        try
        {
            object result = Make(context, assembly, scope, sides, name, "hole", extent =>
            {
                Array parts = scope.Cast<object>().ToArray();
                Array profileArray = profiles.Cast<object>().ToArray();
                FeaturePropertyConstants extentSide = extent;
                double length = depth;
                KeyPointExtentConstants flags = 0;
                return made = holes.Add((uint)parts.Length, ref parts, (uint)profileArray.Length, ref profileArray, ref extentSide, data, extentType, ref length,
                    null, null, null, ref flags);
            }, "Check that the centers are over the parts, or give the other direction or a larger depth.");
            holeMade = true;
            profiles.ForEach(Hide);
            return counterbore ? Counterbore(context, assembly, scope, (AsmRefPlane)refPlane, centers, bore, made!, boreName) : result;
        }
        catch (Exception ex) when (ex is ToolException || holeMade && !ComErrors.IsGone(ex))
        {
            if (holeMade)
            {
                Discard(made!);
            }

            sets.ForEach(Late.TryDelete);
            throw;
        }
    }

    /// <summary>
    /// The counterbore of an assembly hole: an extruded cutout of circles of the counterbore diameter at the hole centers, on the
    /// hole's plane and side, to the counterbore depth, in the parts it reaches. Verified: with several circles in one profile only
    /// the first is cut, so each center gets its own profile; a part of the scope that the cutout does not reach leaves it with a
    /// warning, SetScopeParts is not implemented (E_NOTIMPL) and discarding a trial cutout lost the base plane it was drawn on, so
    /// the parts are chosen beforehand by their range boxes.
    /// </summary>
    private static object Counterbore(SeContext context, AssemblyDocument assembly, Occurrence[] scope, AsmRefPlane refPlane,
        List<(double X, double Y, double Z)> centers, (double Diameter, double Depth) bore, AssemblyFeaturesHole hole, string? name)
    {
        FeaturePropertyConstants side = hole.GetExtentSide();
        Occurrence[] reached = Reached(refPlane, side, scope, centers, Units.MmToM(bore.Diameter) / 2, Units.MmToM(bore.Depth));
        var circles = new List<Profile>();
        foreach ((double x, double y, double z) in centers)
        {
            Profile profile = assembly.ProfileSets.Add().Profiles.Add(refPlane);
            profile.Convert3DCoordinate(x, y, z, out double u, out double v);
            profile.Circles2d.AddByCenterRadius(u, v, Units.MmToM(bore.Diameter) / 2);
            profile.End(ProfileValidationType.igProfileClosed);
            circles.Add(profile);
            try
            {
                SketchService.Define(profile);
            }
            catch (ToolException)
            {
                circles.ForEach(c => Late.TryDelete(c.Parent));
                throw;
            }
        }

        AssemblyFeaturesExtrudedCutouts cutouts = FeaturesOf(assembly).AssemblyFeaturesExtrudedCutouts;
        object cut = Make(context, assembly, reached.Length > 0 ? reached : scope, [side], name, "extruded_cutout", extent =>
        {
            Array parts = (reached.Length > 0 ? reached : scope).Cast<object>().ToArray();
            Array profiles = circles.Cast<object>().ToArray();
            FeaturePropertyConstants extentSide = extent;
            double length = Units.MmToM(bore.Depth);
            KeyPointExtentConstants flags = 0;
            return cutouts.Add(parts.Length, ref parts, profiles.Length, ref profiles, FeaturePropertyConstants.igFinite, ref extentSide,
                FeaturePropertyConstants.igLeft, ref length, null, ref flags, null, null);
        }, "Check that the counterbore circles lie over the parts at the start of the hole.");
        circles.ForEach(Hide);
        return new
        {
            hole = Describe(context, assembly, hole, "hole"),
            counterbore = cut,
            counterboreDiameter = bore.Diameter,
            counterboreDepth = bore.Depth,
            note = "The counterbore is a separate extruded cutout of the assembly: delete both features to remove the hole.",
        };
    }

    /// <summary>
    /// The parts whose range box (assembly coordinates, m) overlaps the box of a counterbore cylinder: from each center projected
    /// onto the plane, along its normal (igRight) or against it (igLeft), to the depth. A part whose box cannot be read is kept.
    /// </summary>
    private static Occurrence[] Reached(AsmRefPlane plane, FeaturePropertyConstants side, Occurrence[] scope,
        List<(double X, double Y, double Z)> centers, double radius, double depth)
    {
        Array normalArray = new double[3];
        Array rootArray = new double[3];
        plane.GetNormal(ref normalArray);
        plane.GetRootPoint(ref rootArray);
        double[] n = [.. normalArray.Cast<double>()];
        double[] root = [.. rootArray.Cast<double>()];
        double sign = side == FeaturePropertyConstants.igLeft ? -1 : 1;
        var boxes = new List<(double[] Lo, double[] Hi)>();
        foreach ((double x, double y, double z) in centers)
        {
            double[] p = [x, y, z];
            double offset = (p[0] - root[0]) * n[0] + (p[1] - root[1]) * n[1] + (p[2] - root[2]) * n[2];
            double[] lo = new double[3];
            double[] hi = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double start = p[k] - offset * n[k];
                double end = start + sign * depth * n[k];
                double grow = radius * Math.Sqrt(Math.Max(0, 1 - n[k] * n[k]));
                lo[k] = Math.Min(start, end) - grow;
                hi[k] = Math.Max(start, end) + grow;
            }

            boxes.Add((lo, hi));
        }

        const double touch = 1e-6;
        return scope.Where(occurrence =>
        {
            Array lo = new double[3];
            Array hi = new double[3];
            try
            {
                occurrence.GetRangeBox(ref lo, ref hi);
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                return true;
            }

            double[] a = [.. lo.Cast<double>()];
            double[] b = [.. hi.Cast<double>()];
            return boxes.Any(box => Enumerable.Range(0, 3).All(k => a[k] < box.Hi[k] - touch && b[k] > box.Lo[k] + touch));
        }).ToArray();
    }
    /// <summary>
    /// Mirror of assembly cutouts (by name) about an assembly plane, in the parts the cutouts go through. Verified: the mirror is
    /// made with MirrorType 1, while igPatternMirror (59) leaves it failed; a cutout mirrored outside the parts is failed too. A
    /// hole mirrored outside the parts is reported fine, so holes are refused.
    /// </summary>
    public static object Mirror(SeContext context, string? documentHandle, string[]? featureNames, string? plane, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        if (featureNames is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'features' must list the names of the features to mirror (action 'list').");
        }

        if (string.IsNullOrWhiteSpace(plane))
        {
            throw ToolException.InvalidArgument("'plane' is required: an assembly plane '1'-'3' or an assembly reference plane handle.");
        }

        object[] parents = featureNames.Select(n => Find(assembly, n)
            ?? throw ToolException.InvalidArgument($"The assembly has no feature '{n}'.", "List them with action 'list'.")).ToArray();
        if (parents.FirstOrDefault(p => p is not (AssemblyFeaturesExtrudedCutout or AssemblyFeaturesRevolvedCutout)) is { } other)
        {
            throw ToolException.InvalidArgument($"'{NameOf(other)}' is not a cutout: only extruded and revolved cutouts are mirrored (a mirrored hole is reported fine even outside the parts).",
                "Make the mirrored holes with action 'hole' at the mirrored centers.");
        }

        CheckName(assembly, name);
        object mirrorPlane = SketchService.AssemblyPlane(context, assembly, plane);
        Array features = parents;
        AssemblyFeaturesMirror mirror = FeaturesOf(assembly).AssemblyFeaturesMirrors.Add(parents.Length, ref features, mirrorPlane,
            (FeaturePropertyConstants)1);
        string? status = StatusOf(mirror);
        if (status is not ("ok" or "warning") || FacesOf(mirror).Length == 0)
        {
            // A failed mirror is deleted without losing planes (verified: the base planes stay).
            TryDeleteFeature(mirror);
            throw new ToolException("feature_failed", $"Solid Edge did not make the mirror in the parts (its status is {status}) and it was removed.",
                "Check that the plane mirrors the features onto the parts they cut.");
        }

        if (name is not null)
        {
            mirror.Name = name;
        }

        object described = Describe(context, assembly, mirror, "mirror");
        return status == "ok" ? described : new { feature = described, warnings = new[] { "Solid Edge reports a warning on this feature." } };
    }

    /// <summary>
    /// Deletes an assembly feature by name with its sketch (see <see cref="Discard"/>: deleting it alone also deletes a base plane,
    /// verified); the parts get back the material it removed.
    /// </summary>
    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object feature = string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the feature name (action 'list').")
            : Find(assembly, name) ?? throw ToolException.InvalidArgument($"The assembly has no feature '{name}'.", "List them with action 'list'.");
        Discard(feature);
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
    /// Refuses a sketch that another feature already uses: removing a failed feature removes its sketch (see Discard), which
    /// would leave the other feature without it. Profiles are compared by name, unique in the assembly.
    /// </summary>
    private static void CheckUnused(AssemblyDocument assembly, Profile profile)
    {
        string? profileName = Late.TryGetString(profile, "Name");
        if (profileName is null)
        {
            return;
        }

        foreach ((object feature, _) in Features(assembly))
        {
            if (ProfilesOf(feature).Any(p => Late.TryGetString(p, "Name") == profileName))
            {
                throw ToolException.InvalidArgument($"The sketch is already used by '{NameOf(feature)}'.", "Draw a new sketch for each feature (se_sketch create).");
            }
        }
    }

    /// <summary>
    /// Creates the feature on the first side and, while it is not fine or misses a part of the scope, turns it to the next side
    /// (SetExtentSide on the same feature, verified: a failed cutout became fine). If no side works, it is discarded.
    /// </summary>
    private static object Make(SeContext context, AssemblyDocument assembly, Occurrence[] scope, FeaturePropertyConstants[] sides,
        string? name, string type, Func<FeaturePropertyConstants, object> add, string hint)
    {
        string? problem = null;
        object? feature = null;
        foreach (FeaturePropertyConstants extent in sides)
        {
            try
            {
                if (feature is null)
                {
                    feature = add(extent);
                }
                else
                {
                    SetSide(feature, extent);
                }
            }
            catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
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
        }

        if (feature is not null)
        {
            Discard(feature);
        }

        throw new ToolException("feature_failed", $"Solid Edge did not make the {type.Replace('_', ' ')} in the parts ({problem}); it was removed{(type == "hole" ? "" : " with its sketch")}.", hint);
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, object feature, string type)
    {
        Array parts = Array.Empty<object>();
        FeaturePropertyConstants? extent = null;
        FeaturePropertyConstants? side = null;
        double? depth = null;
        double? angle = null;
        string[]? mirrorOf = null;
        string[]? patternOf = null;
        SolidEdgeGeometry.Face[]? mirrorFaces = null;
        double? volume = null;
        double[]? range = null;
        double[]? legs = null;
        object? box = null;
        switch (feature)
        {
            case AssemblyFeaturesPattern pattern:
                // Like a mirror: the parts of the features it repeats, and its faces as a whole (FacesForScopePart fails on it, verified).
                object[] repeated = ParentFeatures(pattern);
                parts = repeated.SelectMany(p => ScopeParts(assembly, p)).GroupBy(o => o.Name, StringComparer.Ordinal)
                    .Select(g => g.First()).ToArray();
                patternOf = repeated.Select(NameOf).OfType<string>().ToArray();
                mirrorFaces = FeatureFaces(pattern);
                (double[] lo, double[] hi, int count) = FeatureBox(assembly, pattern);
                box = count > 0 ? Box(lo, hi) : null;
                break;
            case AssemblyFeaturesExtrudedProtrusion protrusion:
                extent = protrusion.GetExtentType();
                side = protrusion.GetExtentSide();
                depth = Geo.Mm(protrusion.GetDepth());
                volume = BodyVolume(protrusion);
                range = FeatureRange(protrusion);
                break;
            case AssemblyFeaturesRevolvedProtrusion revolvedProtrusion:
                extent = revolvedProtrusion.GetExtentType();
                side = revolvedProtrusion.GetExtentSide();
                double turn = Math.Round(Units.RadToDeg(revolvedProtrusion.GetAngle()), 6);
                angle = turn < 1e-9 ? 360 : turn;
                volume = BodyVolume(revolvedProtrusion);
                range = FeatureRange(revolvedProtrusion);
                break;
            case AssemblyFilletWeld weld:
                legs = [Geo.Mm(weld.BaseThickness), Geo.Mm(weld.TargetThickness)];
                volume = BodyVolume(weld);
                range = FeatureRange(weld);
                break;
            case AssemblyLabelWeld:
                break;
            case AssemblyFeaturesMirror mirror:
                // A mirror has no scope parts of its own and FacesForScopePart fails on it (verified): it lists the parts of the
                // features it copies, and its faces as a whole.
                object[] parents = ParentFeatures(mirror);
                parts = parents.SelectMany(p => ScopeParts(assembly, p)).GroupBy(o => o.Name, StringComparer.Ordinal)
                    .Select(g => g.First()).ToArray();
                mirrorOf = parents.Select(NameOf).OfType<string>().ToArray();
                mirrorFaces = FacesOf(mirror);
                break;
            case AssemblyFeaturesExtrudedCutout cut:
                parts = ScopeParts(assembly, cut);
                extent = cut.GetExtentType();
                side = cut.GetExtentSide();
                depth = extent == FeaturePropertyConstants.igFinite ? Geo.Mm(cut.GetDepth()) : null;
                break;
            case AssemblyFeaturesRevolvedCutout revolved:
                parts = ScopeParts(assembly, revolved);
                extent = revolved.GetExtentType();
                side = revolved.GetExtentSide();
                // A full turn is read back as 0.
                double turned = Math.Round(Units.RadToDeg(revolved.GetAngle()), 6);
                angle = turned < 1e-9 ? 360 : turned;
                break;
            case AssemblyFeaturesHole hole:
                parts = ScopeParts(assembly, hole);
                extent = hole.GetExtentType();
                side = hole.GetExtentSide();
                depth = extent == FeaturePropertyConstants.igFinite ? Geo.Mm(hole.GetDepth()) : null;
                break;
            default:
                throw new InvalidOperationException($"Unexpected assembly feature {feature.GetType().Name}.");
        }

        Dictionary<string, Occurrence> listed = AssemblyService.Items<Occurrence>(assembly.Occurrences)
            .GroupBy(o => o.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return new
        {
            name = NameOf(feature),
            type,
            status = StatusOf(feature),
            extent = extent switch
            {
                null => null,
                FeaturePropertyConstants.igFinite => "finite",
                FeaturePropertyConstants.igThroughAll => "through_all",
                var other => other.ToString(),
            },
            depth,
            angle,
            side = side switch
            {
                null => null,
                FeaturePropertyConstants.igRight => "normal",
                FeaturePropertyConstants.igLeft => "reverse",
                FeaturePropertyConstants.igSymmetric => "symmetric",
                var other => other.ToString(),
            },
            mirrorOf,
            patternOf,
            legs,
            volume = volume is { } v ? Geo.Mm3(v) : (double?)null,
            boundingBox = range is null ? box : Box(range[..3], range[3..]),
            faces = mirrorFaces?.Length,
            area = mirrorFaces is null ? (double?)null : Geo.Mm2(mirrorFaces.Sum(f => f.Area)),
            note = mirrorFaces is null ? null : $"Faces and area of the {(patternOf is null ? "mirror" : "copies")} in its first part: Solid Edge gives a feature's faces in its first part only.",
            parts = parts.OfType<Occurrence>().Select(o =>
            {
                SolidEdgeGeometry.Face[]? faces = mirrorFaces is null ? FacesOn(feature, o) : null;
                return new
                {
                    // The scope parts are other COM objects for the same occurrences (verified: each one got a new handle);
                    // the occurrence as the assembly lists it keeps the handle the caller already has.
                    occurrence = context.Register(listed.GetValueOrDefault(o.Name) ?? o, "occurrence", (SolidEdgeDocument)assembly),
                    name = o.Name,
                    faces = faces?.Length,
                    area = faces is null ? (double?)null : Geo.Mm2(faces.Sum(f => f.Area)),
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
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    private static IEnumerable<(object Feature, string Type)> Features(AssemblyDocument assembly)
    {
        AssemblyFeatures features = FeaturesOf(assembly);
        return Items<AssemblyFeaturesExtrudedCutout>(features.AssemblyFeaturesExtrudedCutouts).Select(f => ((object)f, "extruded_cutout"))
            .Concat(Items<AssemblyFeaturesRevolvedCutout>(features.AssemblyFeaturesRevolvedCutouts).Select(f => ((object)f, "revolved_cutout")))
            .Concat(Items<AssemblyFeaturesHole>(features.AssemblyFeaturesHoles).Select(f => ((object)f, "hole")))
            .Concat(Items<AssemblyFeaturesMirror>(features.AssemblyFeaturesMirrors).Select(f => ((object)f, "mirror")))
            .Concat(Items<AssemblyFeaturesPattern>(features.AssemblyFeaturesPatterns).Select(f => ((object)f, "pattern")))
            .Concat(Items<AssemblyFeaturesExtrudedProtrusion>(features.ExtrudedProtrusions).Select(f => ((object)f, "protrusion")))
            .Concat(Items<AssemblyFeaturesRevolvedProtrusion>(features.RevolvedProtrusions).Select(f => ((object)f, "revolved_protrusion")))
            .Concat(Welds<AssemblyFilletWeld>(() => features.FilletWelds).Select(f => ((object)f, "fillet_weld")))
            .Concat(Welds<AssemblyLabelWeld>(() => features.LabelWelds).Select(f => ((object)f, "label_weld")));
    }

    /// <summary>Welds of the assembly; none if the collection cannot be read.</summary>
    private static IEnumerable<T> Welds<T>(Func<object> collection)
    {
        try
        {
            return [.. Items<T>(collection())];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Parts a cutout or hole goes through. Verified: in an assembly reopened from its file, GetScopeParts can hand back an
    /// occurrence that is already disconnected (RPC_E_DISCONNECTED on any call); the parts are then the occurrences on which
    /// the feature leaves faces.
    /// </summary>
    private static Occurrence[] ScopeParts(AssemblyDocument assembly, object feature)
    {
        Array parts = Array.Empty<object>();
        switch (feature)
        {
            case AssemblyFeaturesExtrudedCutout cut:
                cut.GetScopeParts(out _, out parts);
                break;
            case AssemblyFeaturesRevolvedCutout revolved:
                revolved.GetScopeParts(out _, out parts);
                break;
            case AssemblyFeaturesHole hole:
                hole.GetScopeParts(out _, out parts);
                break;
            default:
                return [];
        }

        Occurrence[] given = [.. (parts ?? Array.Empty<object>()).OfType<Occurrence>()];
        return given.All(Alive)
            ? given
            : [.. Items<Occurrence>(assembly.Occurrences).Where(o => !o.Subassembly && FacesOn(feature, o).Length > 0)];
    }

    private static bool Alive(Occurrence occurrence)
    {
        try
        {
            _ = occurrence.Name;
            return true;
        }
        catch (COMException ex) when (ex.HResult == ComErrors.RpcEDisconnected)
        {
            return false;
        }
    }

    /// <summary>Features a mirror copies.</summary>
    private static object[] ParentFeatures(AssemblyFeaturesMirror mirror) =>
        OutArray(mirror, typeof(AssemblyFeaturesMirror), nameof(AssemblyFeaturesMirror.GetParentFeatures));

    /// <summary>Faces the mirror leaves in the parts; none if it cannot be read.</summary>
    private static SolidEdgeGeometry.Face[] FacesOf(AssemblyFeaturesMirror mirror)
    {
        try
        {
            return [.. Late.Items((Faces)mirror.Faces[TopologyQuery.igQueryAll]).OfType<SolidEdgeGeometry.Face>()];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }
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

    /// <summary>
    /// Removes a feature, its profiles first. Deleting the feature alone also deletes its profile and the reference
    /// plane the profile is drawn on (verified: each failed cutout or hole took a base plane of the assembly with it, and later
    /// sketches on '1' landed on another plane); with the profiles deleted first, the planes stay.
    /// </summary>
    private static void Discard(object feature)
    {
        foreach (object profile in ProfilesOf(feature))
        {
            Late.TryDelete(profile);
        }

        TryDeleteFeature(feature);
    }

    private static void SetSide(object feature, FeaturePropertyConstants side)
    {
        switch (feature)
        {
            case AssemblyFeaturesExtrudedCutout cut:
                cut.SetExtentSide(side);
                break;
            case AssemblyFeaturesRevolvedCutout revolved:
                revolved.SetExtentSide(side);
                break;
            case AssemblyFeaturesHole hole:
                hole.SetExtentSide(side);
                break;
            default:
                throw new InvalidOperationException($"Unexpected assembly feature {feature.GetType().Name}.");
        }
    }

    private static object[] ProfilesOf(object feature)
    {
        try
        {
            return feature switch
            {
                AssemblyFeaturesExtrudedCutout => OutArray(feature, typeof(AssemblyFeaturesExtrudedCutout), nameof(AssemblyFeaturesExtrudedCutout.GetProfiles)),
                AssemblyFeaturesRevolvedCutout => OutArray(feature, typeof(AssemblyFeaturesRevolvedCutout), nameof(AssemblyFeaturesRevolvedCutout.GetProfiles)),
                AssemblyFeaturesHole => OutArray(feature, typeof(AssemblyFeaturesHole), nameof(AssemblyFeaturesHole.GetProfiles)),
                AssemblyFeaturesExtrudedProtrusion => OutArray(feature, typeof(AssemblyFeaturesExtrudedProtrusion), nameof(AssemblyFeaturesExtrudedProtrusion.GetProfiles)),
                AssemblyFeaturesRevolvedProtrusion => OutArray(feature, typeof(AssemblyFeaturesRevolvedProtrusion), nameof(AssemblyFeaturesRevolvedProtrusion.GetProfiles)),
                AssemblyFeaturesPattern pattern => pattern.GetProfile() is { } profile ? [profile] : [],
                _ => [],
            };
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException && !ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Array from a method (int count, Array items) with both out. Called directly, the out array goes as null and fails
    /// (verified on GetParentFeatures: NullReferenceException); the interop method invoked with an empty array works.
    /// </summary>
    private static object[] OutArray(object target, Type type, string method)
    {
        object?[] args = [0, Array.Empty<object>()];
        type.GetMethod(method)!.Invoke(target, args);
        return args[1] is Array items ? [.. items.Cast<object>()] : [];
    }

    private static void TryDeleteFeature(object feature)
    {
        try
        {
            DeleteFeature(feature);
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex) && !ComErrors.IsGone(ex))
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
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // Affects only the presentation; the feature is already created and verified.
        }
    }
}
