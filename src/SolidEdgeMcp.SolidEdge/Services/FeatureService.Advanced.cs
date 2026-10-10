using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using OffsetType = SolidEdgeFrameworkSupport.PatternOffsetTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Hole parameters (mm and degrees). The centers are in model coordinates and are projected onto the plane.</summary>
public sealed record HoleSpec(
    string Plane,
    double[]? Centers,
    double? Diameter,
    string Type = "simple",
    double? CounterboreDiameter = null,
    double? CounterboreDepth = null,
    double? CountersinkDiameter = null,
    double CountersinkAngle = 90,
    string? Thread = null,
    string Extent = "through_all",
    double? Depth = null,
    string Direction = "auto");

/// <summary>Pattern parameters (mm and degrees).</summary>
public sealed record PatternSpec(
    string[]? Features,
    string Type,
    string? Plane,
    int CountX = 1,
    int CountY = 1,
    double? SpacingX = null,
    double? SpacingY = null,
    int Count = 0,
    double Angle = 360,
    double[]? Center = null);

public static partial class FeatureService
{
    public static readonly IReadOnlyList<string> HoleTypes = ["simple", "counterbore", "countersink", "tapped"];
    public static readonly IReadOnlyList<string> PatternTypes = ["rectangular", "circular", "mirror"];

    /// <summary>
    /// Holes at the given centers. With direction "auto" it tries toward the normal of the plane and, if it
    /// removes no material, toward the opposite side (as a person would when looking at the preview).
    /// </summary>
    public static object Hole(SeContext context, string? documentHandle, HoleSpec spec)
    {
        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Plane);
        List<(double X, double Y, double Z)> centers = Geo.Points3(spec.Centers, "centers", 1);
        FeaturePropertyConstants holeType = spec.Type switch
        {
            "simple" => FeaturePropertyConstants.igRegularHole,
            "counterbore" => FeaturePropertyConstants.igCounterboreHole,
            "countersink" => FeaturePropertyConstants.igCountersinkHole,
            // igTappedHole creates in Solid Edge 2026 a hole with a stepped counterbore (counterdrill) with dimensions
            // by default: the thread is a simple drilled hole with the thread data (cosmetic thread).
            "tapped" => FeaturePropertyConstants.igRegularHole,
            _ => throw ToolException.InvalidArgument($"Unknown hole type '{spec.Type}'.", $"Use one of: {string.Join(", ", HoleTypes)}."),
        };
        bool finite = spec.Extent switch
        {
            "finite" => true,
            "through_all" or "through_next" => false,
            _ => throw ToolException.InvalidArgument($"Unknown extent '{spec.Extent}'.", "Use finite, through_all or through_next."),
        };
        double depth = finite
            ? Units.MmToM(Units.RequirePositive(spec.Depth ?? throw ToolException.InvalidArgument("Extent 'finite' requires 'depth' (mm)."), "depth"))
            : 0;
        FeaturePropertyConstants[] sides = spec.Direction switch
        {
            "auto" => [FeaturePropertyConstants.igRight, FeaturePropertyConstants.igLeft],
            "normal" => [FeaturePropertyConstants.igRight],
            "reverse" => [FeaturePropertyConstants.igLeft],
            _ => throw ToolException.InvalidArgument($"Unknown direction '{spec.Direction}'.", "Use auto, normal or reverse."),
        };

        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        object planeSource = model.ResolvePlaneSource(context, spec.Plane);
        List<RefPlane>? facePlanes = null;
        if (planeSource is SolidEdgeGeometry.Face face)
        {
            // Each hole needs its own local plane on the face, and they must be created before changing the model,
            // because afterwards the face is no longer valid. That is also why the other side cannot be retried: with "auto"
            // the direction toward the material, opposite to the outward normal, is chosen up front.
            facePlanes = centers.Select(_ => model.PlaneFrom(face)).ToList();
            if (spec.Direction == "auto")
            {
                sides = [IntoMaterial(face, facePlanes[0])];
            }
        }

        HoleData data = CreateHoleData(model, spec, holeType, depth);
        double before = model.Volume();
        double current = before;
        bool warning = false;
        // In ordered modelling a hole uses a single profile and, when it is closed, Solid Edge separates each circle into its
        // own profile (Holes.AddSync does not work and Hole.SetProfiles leaves the feature failed): one feature is created
        // per center and, if one fails, all are undone so that the work is not left half done.
        var holes = new List<(object Feature, IncludedProfile Included)>();
        try
        {
            for (int k = 0; k < centers.Count; k++)
            {
                for (int i = 0; ; i++)
                {
                    FeaturePropertyConstants side = sides[i];
                    RefPlane refPlane = facePlanes?[k] ?? model.PlaneFrom(planeSource);
                    ProfileSet set = model.ProfileSets.Add();
                    Profile profile = set.Profiles.Add(refPlane);
                    profile.Convert3DCoordinate(centers[k].X, centers[k].Y, centers[k].Z, out double u, out double v);
                    profile.Holes2d.Add(u, v);
                    profile.End(ProfileValidationType.igProfileClosed);
                    var included = new IncludedProfile(profile, set);
                    try
                    {
                        object feature = Create("hole", included, () => spec.Extent switch
                        {
                            "finite" => target.Holes.AddFinite(profile, side, depth, data),
                            "through_all" => target.Holes.AddThroughAll(profile, side, data),
                            _ => target.Holes.AddThroughNext(profile, side, data),
                        });
                        (current, string? status) = Check(context, model, feature, current, -1, null, included);
                        Hide(profile);
                        holes.Add((feature, included));
                        warning |= status == "warning";
                        sides = [side];
                        break;
                    }
                    catch (ToolException ex) when (ex.Code == "feature_failed" && i + 1 < sides.Length)
                    {
                        // "auto": the other side of the plane is tried; the following centers use the one that works.
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ToolException or COMException && !ComErrors.IsDisconnected(ex))
        {
            foreach ((object feature, IncludedProfile included) in holes)
            {
                TryDelete(feature);
                DeleteIncluded(included);
            }

            // Local planes that no hole got to use (those of deleted features disappear with them).
            foreach (RefPlane plane in facePlanes ?? [])
            {
                TryDelete(plane);
            }

            if (holes.Count == 0 || ex is not ToolException failure)
            {
                throw;
            }

            context.ModelChanged(model.Document, "the model changed (holes undone)");
            string at = string.Join(", ", new[] { centers[holes.Count].X, centers[holes.Count].Y, centers[holes.Count].Z }
                .Select(c => Geo.Mm(c).ToString(CultureInfo.InvariantCulture)));
            throw new ToolException(failure.Code,
                $"Hole {holes.Count + 1} of {centers.Count} at [{at}] mm failed, so the {holes.Count} hole(s) already made in this call were deleted too. {failure.Message}",
                failure.NextStep, failure);
        }

        return new
        {
            feature = Describe(context, model, holes[0].Feature),
            features = holes.Count > 1 ? holes.Select(h => Describe(context, model, h.Feature)).ToList() : null,
            volume = Geo.Mm3(current),
            volumeChange = Geo.Mm3(current - before),
            direction = sides[0] == FeaturePropertyConstants.igRight ? "normal" : "reverse",
            warnings = Warnings(switched, warning),
        };
    }

    /// <summary>Rectangular or circular pattern of features, or mirror about a plane.</summary>
    public static object Pattern(SeContext context, string? documentHandle, PatternSpec spec)
    {
        if (spec.Features is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'features' must list at least one feature handle.", "Get them from se_feature_tree or from the tool that created them.");
        }

        if (string.IsNullOrWhiteSpace(spec.Plane))
        {
            throw ToolException.InvalidArgument(spec.Type == "mirror"
                ? "'plane' is required: the mirror plane ('1'-'3', refplane or planar face handle)."
                : "'plane' is required: the plane where the pattern is laid out (normally the sketch plane of the features).");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, spec.Features[0]);
        object[] features = spec.Features.Select(h => context.Handles.Resolve(h, "features")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        RefPlane refPlane = model.ResolvePlane(context, spec.Plane);
        double before = model.Volume();
        object feature;
        switch (spec.Type)
        {
            case "rectangular":
            {
                if (spec.CountX < 1 || spec.CountY < 1 || spec.CountX * spec.CountY < 2)
                {
                    throw ToolException.InvalidArgument("'countX' and 'countY' must be at least 1, with 2 or more occurrences in total.");
                }

                double sx = spec.CountX > 1 ? Units.MmToM(Units.RequirePositive(spec.SpacingX ?? throw ToolException.InvalidArgument("'spacingX' (mm) is required when countX > 1."), "spacingX")) : 0.001;
                double sy = spec.CountY > 1 ? Units.MmToM(Units.RequirePositive(spec.SpacingY ?? throw ToolException.InvalidArgument("'spacingY' (mm) is required when countY > 1."), "spacingY")) : 0.001;
                feature = Create("rectangular pattern", () => target.Patterns.AddByRectangularEx(features.Length, features, refPlane,
                    spec.CountX, spec.CountY, sx, sy, 0.0, OffsetType.sePatternFixedOffset, 1, PatternTypeConstants.seSmartPattern));
                break;
            }

            case "circular":
            {
                if (spec.Count < 2)
                {
                    throw ToolException.InvalidArgument("'count' must be at least 2 for a circular pattern.");
                }

                double total = spec.Angle;
                if (total <= 0 || total > 360)
                {
                    throw ToolException.InvalidArgument("'angle' must be greater than 0 and at most 360 degrees.");
                }

                (double cx, double cy, double cz) = spec.Center is null ? (0, 0, 0) : Geo.Points3(spec.Center, "center", 1).Single();
                Array axisPoint = new[] { cx, cy, cz };
                bool arc = total < 360 - 1e-9;
                feature = Create("circular pattern", () => target.Patterns.AddByCircularEx(features.Length, features, refPlane,
                    spec.Count, Units.DegToRad(total), axisPoint, OffsetType.sePatternFitOffset,
                    PatternCurveAnchorSideConstants.sePatternCurveLeftSide, arc, PatternTypeConstants.seSmartPattern));
                break;
            }

            case "mirror":
                feature = Create("mirror copy", () => target.MirrorCopies.Add(refPlane, features.Length, features));
                break;
            default:
                throw ToolException.InvalidArgument($"Unknown pattern type '{spec.Type}'.", $"Use one of: {string.Join(", ", PatternTypes)}.");
        }

        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>Shell (thin wall) with a common thickness; the open faces are removed.</summary>
    public static object Shell(SeContext context, string? documentHandle, double? thickness, string[]? openFaces, string side)
    {
        string? related = openFaces is { Length: > 0 } ? openFaces[0] : null;
        ModelDocument model = ModelDocument.For(context, documentHandle, related ?? "");
        double t = Units.MmToM(Units.RequirePositive(thickness ?? throw ToolException.InvalidArgument("se_shell requires 'thickness' (mm)."), "thickness"));
        FeaturePropertyConstants thicknessSide = side switch
        {
            "inside" => FeaturePropertyConstants.igInside,
            "outside" => FeaturePropertyConstants.igOutside,
            _ => throw ToolException.InvalidArgument($"Unknown side '{side}'.", "Use inside or outside."),
        };
        object[] faces = (openFaces ?? []).Select(h => (object)context.Handles.Resolve<SolidEdgeGeometry.Face>(h, "openFaces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        double before = model.Volume();
        object feature = Create("thin wall", () => faces.Length == 0
            ? target.Thinwalls.Add(thicknessSide, t)
            : target.Thinwalls.Add(thicknessSide, t, faces));
        return Verify(context, model, feature, before, 0, switched);
    }

    /// <summary>Draft of faces relative to a neutral plane.</summary>
    public static object Draft(SeContext context, string? documentHandle, string plane, string[]? faces, double? angle, string direction)
    {
        if (faces is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'faces' must list at least one face handle.", "Get face handles with se_geometry action 'faces'.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, faces[0]);
        double radians = Units.DegToRad(Units.RequirePositive(angle ?? throw ToolException.InvalidArgument("se_draft requires 'angle' (degrees)."), "angle"));
        if (radians >= Math.PI / 2)
        {
            throw ToolException.InvalidArgument("'angle' must be less than 90 degrees.");
        }

        // Drafts.Add rejects igLeft/igRight (E_FAIL). Verified on a block with the neutral plane at the bottom and at the top:
        // igOutside narrows the part toward the normal of the plane (drafting in that direction) and igInside widens it.
        FeaturePropertyConstants side = direction switch
        {
            "normal" => FeaturePropertyConstants.igOutside,
            "reverse" => FeaturePropertyConstants.igInside,
            _ => throw ToolException.InvalidArgument($"Unknown direction '{direction}'.", "Use normal or reverse."),
        };
        object[] faceObjects = faces.Select(h => (object)context.Handles.Resolve<SolidEdgeGeometry.Face>(h, "faces")).ToArray();
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        RefPlane refPlane = model.ResolvePlane(context, plane);
        double before = model.Volume();
        double areaBefore = model.SurfaceArea();
        double[] angles = Enumerable.Repeat(radians, faceObjects.Length).ToArray();
        object feature = Create("draft", () => target.Drafts.Add(refPlane, faceObjects.Length, faceObjects, angles, side));
        return Verify(context, model, feature, before, 0, switched,
            changedOtherwise: () => Math.Abs(model.SurfaceArea() - areaBefore) > AreaTolerance);
    }

    /// <summary>Side of the local plane of a face toward the inside of the material.</summary>
    private static FeaturePropertyConstants IntoMaterial(SolidEdgeGeometry.Face face, RefPlane plane)
    {
        double[] outward = InspectService.FaceNormal(face);
        Array normal = new double[3];
        plane.GetNormal(ref normal);
        double dot = Geo.Doubles(normal).Zip(outward, (a, b) => a * b).Sum();
        return dot > 0 ? FeaturePropertyConstants.igLeft : FeaturePropertyConstants.igRight;
    }

    private static HoleData CreateHoleData(ModelDocument model, HoleSpec spec, FeaturePropertyConstants type, double depth)
    {
        HoleDataCollection collection = model.HoleData;
        if (spec.Type == "tapped")
        {
            (double nominal, double pitch, string description) = ParseMetricThread(spec.Thread);
            double drill = spec.Diameter is { } d ? Units.MmToM(Units.RequirePositive(d, "diameter")) : nominal - pitch;
            // Verified: a finite threaded hole fails if Add does not receive ThreadDepthMethod and ThreadDepth; with
            // igFinite and the hole depth, the hole is created (although HoleData.ThreadDepthMethod is read back afterwards as igNone).
            return depth > 0
                ? collection.Add(type, drill,
                    ThreadMinorDiameter: nominal - 1.0825 * pitch,
                    ThreadDepthMethod: FeaturePropertyConstants.igFinite,
                    ThreadDepth: depth,
                    ThreadExternalDiameter: nominal,
                    ThreadDescription: description)
                : collection.Add(type, drill,
                    ThreadMinorDiameter: nominal - 1.0825 * pitch,
                    ThreadExternalDiameter: nominal,
                    ThreadDescription: description);
        }

        double diameter = Units.MmToM(Units.RequirePositive(spec.Diameter ?? throw ToolException.InvalidArgument("se_hole requires 'diameter' (mm)."), "diameter"));
        switch (type)
        {
            case FeaturePropertyConstants.igCounterboreHole:
            {
                double cbd = Units.MmToM(Units.RequirePositive(spec.CounterboreDiameter ?? throw ToolException.InvalidArgument("A counterbore hole requires 'counterboreDiameter' (mm)."), "counterboreDiameter"));
                double cbh = Units.MmToM(Units.RequirePositive(spec.CounterboreDepth ?? throw ToolException.InvalidArgument("A counterbore hole requires 'counterboreDepth' (mm)."), "counterboreDepth"));
                if (cbd <= diameter)
                {
                    throw ToolException.InvalidArgument("'counterboreDiameter' must be larger than 'diameter'.");
                }

                return collection.Add(type, diameter, CounterboreDiameter: cbd, CounterboreDepth: cbh);
            }

            case FeaturePropertyConstants.igCountersinkHole:
            {
                double csd = Units.MmToM(Units.RequirePositive(spec.CountersinkDiameter ?? throw ToolException.InvalidArgument("A countersink hole requires 'countersinkDiameter' (mm)."), "countersinkDiameter"));
                if (csd <= diameter)
                {
                    throw ToolException.InvalidArgument("'countersinkDiameter' must be larger than 'diameter'.");
                }

                // HoleData.CountersinkAngle is in degrees, not in radians as in the rest of the API (verified: by
                // default it is 82, and with 90 the volume matches the 90° cone).
                double angle = Units.RequirePositive(spec.CountersinkAngle, "countersinkAngle");
                if (angle >= 180)
                {
                    throw ToolException.InvalidArgument("'countersinkAngle' (degrees) must be below 180.");
                }

                return collection.Add(type, diameter, CountersinkDiameter: csd, CountersinkAngle: angle);
            }

            default:
                return collection.Add(type, diameter);
        }
    }

    /// <summary>Metric thread "M10" or "M10x1.25" → nominal diameter and pitch in meters (ISO coarse pitch if missing).</summary>
    private static (double Nominal, double Pitch, string Description) ParseMetricThread(string? thread)
    {
        Match match = Regex.Match(thread ?? "", @"^\s*M\s*(\d+(?:[.,]\d+)?)\s*(?:[xX×]\s*(\d+(?:[.,]\d+)?))?\s*$");
        if (!match.Success)
        {
            throw ToolException.InvalidArgument("'thread' must be a metric size such as 'M6' or 'M10x1.25'.");
        }

        double nominal = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
        double pitch = match.Groups[2].Success
            ? double.Parse(match.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
            : CoarsePitch(nominal);
        string description = $"M{nominal.ToString(CultureInfo.InvariantCulture)}x{pitch.ToString(CultureInfo.InvariantCulture)}";
        return (Units.MmToM(nominal), Units.MmToM(pitch), description);
    }

    /// <summary>ISO 261 coarse pitch for the usual diameters.</summary>
    private static double CoarsePitch(double nominal) => nominal switch
    {
        <= 1.6 => 0.35,
        <= 2 => 0.4,
        <= 2.5 => 0.45,
        <= 3 => 0.5,
        <= 4 => 0.7,
        <= 5 => 0.8,
        <= 6 => 1.0,
        <= 8 => 1.25,
        <= 10 => 1.5,
        <= 12 => 1.75,
        <= 16 => 2.0,
        <= 20 => 2.5,
        <= 24 => 3.0,
        <= 30 => 3.5,
        <= 36 => 4.0,
        _ => throw ToolException.InvalidArgument($"Give the pitch explicitly for M{nominal.ToString(CultureInfo.InvariantCulture)} (e.g. 'M42x4.5')."),
    };
}
