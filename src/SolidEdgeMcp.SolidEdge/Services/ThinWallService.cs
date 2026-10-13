// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgePart;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Thin-wall features of plastic parts that have no other tool. Verified in Solid Edge 2026: a mounting boss needs the
/// boss's own sketch element (Profile.MountingBoss2dCollection.Add), not a sketch point; MountingBossCollection.Add with
/// that profile grows the boss from the sketch plane to the next face on one side and leaves it failed on the other (a
/// boss of 8 mm at the open top of a box shelled 2 mm, 20 mm high, adds π·4²·18 = 904.78 mm³); several bosses need a profile\n/// each. Vents give E_FAIL with
/// every method (AddFinite, AddThroughAll, AddThroughNext).
/// </summary>
public static class ThinWallService
{
    /// <summary>Mounting bosses at points of a plane, grown to the next face; the side that works is found by trying both.</summary>
    public static object MountingBoss(SeContext context, string? documentHandle, string? plane, double[]? centers, double? diameter,
        double? holeDiameter, double? holeDepth)
    {
        if (string.IsNullOrWhiteSpace(plane))
        {
            throw ToolException.InvalidArgument("mounting_boss needs 'plane': where the bosses start (a reference plane or planar face, e.g. the plane of the open face of a shelled part).");
        }

        if (centers is null || centers.Length < 2 || centers.Length % 2 != 0)
        {
            throw ToolException.InvalidArgument("'centers' takes the boss centres on the plane as [x1, y1, x2, y2, ...] in mm (sketch coordinates of the plane).");
        }

        double boss = Units.RequirePositive(diameter ?? throw ToolException.InvalidArgument("mounting_boss needs 'diameter' (mm)."), "diameter");
        if (holeDiameter is { } hole && (hole <= 0 || hole >= boss))
        {
            throw ToolException.InvalidArgument("'holeDiameter' must be positive and smaller than the boss 'diameter'.");
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, plane);
        bool switched = model.EnsureOrdered();
        Model target = model.RequireModel();
        RefPlane refPlane = model.ResolvePlane(context, plane);
        // Verified: one profile holding two boss elements leaves the feature failed; a profile per boss works.
        var profiles = new List<Profile>();
        for (int i = 0; i < centers.Length; i += 2)
        {
            Profile profile = model.Sketches.Add().Profiles.Add(refPlane);
            profile.MountingBoss2dCollection.Add(Units.MmToM(centers[i]), Units.MmToM(centers[i + 1]));
            profile.End((ProfileValidationType)((int)ProfileValidationType.igProfileClosed | (int)ProfileValidationType.igProfileAllowNested));
            profiles.Add(profile);
        }

        object[] profileArray = [.. profiles];
        double before = model.Volume();
        foreach (FeaturePropertyConstants side in new[] { FeaturePropertyConstants.igLeft, FeaturePropertyConstants.igRight })
        {
            MountingBoss? feature = null;
            try
            {
                feature = holeDiameter is { } h
                    ? target.MountingBossCollection.Add(profiles.Count, profileArray, side, Units.MmToM(boss), true, Units.MmToM(h),
                        holeDepth is { } hd ? Units.MmToM(Units.RequirePositive(hd, "holeDepth")) : Type.Missing)
                    : target.MountingBossCollection.Add(profiles.Count, profileArray, side, Units.MmToM(boss));
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                continue;
            }

            double after = model.Volume();
            if (feature.GetStatusEx(out _) == FeatureStatusConstants.igFeatureOK && after > before)
            {
                return new
                {
                    feature = FeatureService.Describe(context, model, feature),
                    bosses = centers.Length / 2,
                    volume = Geo.Mm3(after),
                    volumeChange = Geo.Mm3(after - before),
                    note = switched ? "The document was switched to ordered modeling." : null,
                };
            }

            TryDelete(feature);
        }

        foreach (Profile profile in profiles)
        {
            TryDelete(profile.Parent);
        }

        throw new ToolException("boss_failed", "Solid Edge made no mounting boss from that plane on either side.",
            "Start the bosses on a plane with a face of the part ahead of it, such as the plane of the open face of a shelled part, and keep the centres over the part.");
    }

    private static void TryDelete(object? value)
    {
        if (value is null)
        {
            return;
        }

        try
        {
            Late.Call(value, "Delete");
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // Left in the feature tree, failed, for the person to see.
        }
    }
}
