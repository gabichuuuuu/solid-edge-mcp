// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeFrameworkSupport;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using Edge = SolidEdgeGeometry.Edge;
using TopologyQuery = SolidEdgeGeometry.FeatureTopologyQueryTypeConstants;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Parameters of a loft, sweep or helix (mm and degrees).</summary>
public sealed record SweepSpec(
    SweepAction Action,
    FeatureOperation Operation = FeatureOperation.Add,
    string[]? Sketches = null,
    string? Path = null,
    double[]? Origins = null,
    string? Axis = null,
    double? Pitch = null,
    double? Turns = null,
    double? Height = null,
    HelixHand Hand = HelixHand.Right,
    ThinWallSpec? ThinWall = null,
    double? Taper = null);

public static partial class FeatureService
{
    /// <summary>Section type: sketch profile (igProfileBasedCrossSection).</summary>
    private const int ProfileSection = (int)FeaturePropertyConstants.igProfileBasedCrossSection;

    /// <summary>Path type: a construction curve (igEdgeBasedCrossSection), e.g. a 3D curve from se_curve.</summary>
    private const int EdgeSection = (int)FeaturePropertyConstants.igEdgeBasedCrossSection;

    /// <summary>
    /// Loft between sections, sweep of sections along a path, or helix of a section
    /// around an axis. As with extrusion and revolve, each sketch is used through a linked copy of its
    /// profile: if the feature fails and is deleted, the sketches are kept.
    /// </summary>
    public static object LoftSweep(SeContext context, string? documentHandle, SweepSpec spec)
    {
        string[] sketches = spec.Sketches is { Length: > 0 } list
            ? list
            : throw ToolException.InvalidArgument("'sketches' must list the closed sketches (sections) of the feature.",
                "Draw each section with se_sketch and close it ('close').");
        int minimum = spec.Action == SweepAction.Loft ? 2 : 1;
        if (sketches.Length < minimum || (spec.Action == SweepAction.Helix && sketches.Length != 1))
        {
            throw ToolException.InvalidArgument(spec.Action switch
            {
                SweepAction.Loft => "A loft needs at least two sections in 'sketches', in order.",
                SweepAction.Helix => "A helix takes exactly one section in 'sketches'.",
                _ => "A sweep needs at least one section in 'sketches'.",
            });
        }

        ModelDocument model = ModelDocument.For(context, documentHandle, sketches[0]);
        bool cut = IsCut(spec.Operation);
        ThinWallSpec? wall = CheckThinWall(spec.ThinWall, cut);
        Profile[] sketchProfiles = sketches.Select(s => SketchService.ResolveDefined(context, s)).ToArray();
        (double X, double Y)[]? origins = spec.Origins is null ? null : Geo.Points2(spec.Origins, "origins", sketches.Length).ToArray();
        if (origins is not null && origins.Length != sketches.Length)
        {
            throw ToolException.InvalidArgument("'origins' must give one point [x, y] per section.");
        }

        Profile? pathProfile = null;
        object? pathCurve = null;
        RefAxis? axis = null;
        (double Height, double Pitch, double Turns) helix = default;
        if (spec.Taper is { } taper && (spec.Action != SweepAction.Helix || !double.IsFinite(taper) || Math.Abs(taper) >= 90))
        {
            throw ToolException.InvalidArgument("'taper' is the angle of a conical helix in degrees, between -90 and 90 (positive widens towards the end).");
        }
        FeaturePropertyConstants hand = FeaturePropertyConstants.igRight;
        switch (spec.Action)
        {
            case SweepAction.Loft:
                break;
            case SweepAction.Sweep:
                string pathHandle = spec.Path ?? throw ToolException.InvalidArgument("A sweep requires 'path'.",
                    "Draw the path as an open or closed chain with se_sketch and close it with 'close' (open: true for an open chain), or make a 3D curve with se_curve.");
                object resolvedPath = context.Handles.Resolve(pathHandle, "path");
                if (resolvedPath is Sketch3D sketch3d)
                {
                    // The edges of a 3D sketch belong to its feature.
                    resolvedPath = sketch3d.Sketch3DFeature;
                }

                if (resolvedPath is Profile or Sketch)
                {
                    pathProfile = SketchService.ResolveDefined(context, pathHandle);
                }
                else if (CurveEdges(resolvedPath).Length > 0)
                {
                    pathCurve = resolvedPath;
                }
                else
                {
                    throw new ToolException("handle_wrong_type", $"'{pathHandle}' in 'path' is neither a sketch nor a curve.",
                        "Pass a sketch closed with se_sketch 'close' (open: true for an open chain) or a curve from se_curve.");
                }

                break;
            case SweepAction.Helix:
                axis = context.Handles.Resolve<RefAxis>(spec.Axis ?? throw ToolException.InvalidArgument("A helix requires 'axis'.",
                    "Draw a line in the section's sketch and turn it into the axis with se_sketch action 'axis', before 'close'."), "axis");
                helix = HelixSize(spec.Pitch, spec.Turns, spec.Height);
                hand = spec.Hand switch
                {
                    HelixHand.Right => FeaturePropertyConstants.igRight,
                    HelixHand.Left => FeaturePropertyConstants.igLeft,
                    _ => throw new ArgumentOutOfRangeException(nameof(spec)),
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(spec));
        }

        bool switched = model.EnsureOrdered();
        double before = model.Volume();
        Model? existing = model.TryModel();
        if (cut && existing is null)
        {
            throw new ToolException("no_model", $"'{model.Document.Name}' has no solid to cut yet.", "Create the first feature with operation 'add'.");
        }

        var included = new List<IncludedProfile>();
        try
        {
            foreach (Profile profile in sketchProfiles)
            {
                included.Add(Include(model, profile));
            }

            IncludedProfile? path = pathProfile is null ? null : Include(model, pathProfile);
            if (path is not null)
            {
                included.Add(path);
            }

            object[] sections = included.Take(sketchProfiles.Length).Select(i => (object)i.Profile).ToArray();
            int[] types = Enumerable.Repeat(ProfileSection, sections.Length).ToArray();
            object[] starts = sections.Select((s, i) => (object)(origins is null ? StartPoint((Profile)s, sketchProfiles[i]) : [origins[i].X, origins[i].Y])).ToArray();
            string what = $"{ArgumentNames.Snake(spec.Action)} {(cut ? "cutout" : "protrusion")}";

            object feature = Create(what, null, () => spec.Action switch
            {
                SweepAction.Loft => Loft(model, existing, cut, sections, types, starts),
                SweepAction.Sweep => Sweep(model, existing, cut,
                    path is null ? EdgePath(pathCurve!) : [path.Profile], path is null ? EdgeSection : ProfileSection, sections, types, starts),
                _ => Helix(model, existing, cut, axis!, sections, helix, hand, spec.Taper),
            });

            // The section copies are deleted by the catch below if the wall does not take.
            ApplyThinWall(model, feature, wall, null);
            (double after, string? status) = Check(context, model, feature, before, cut ? -1 : 1, string.Join(", ", sketches), null);
            foreach (Profile profile in sketchProfiles.Concat(pathProfile is null ? [] : [pathProfile]))
            {
                Hide(profile);
            }

            foreach (IncludedProfile copy in included)
            {
                Hide(copy);
            }

            return new
            {
                feature = Describe(context, model, feature),
                volume = Geo.Mm3(after),
                volumeChange = Geo.Mm3(after - before),
                helix = axis is null ? null : new { height = Geo.Mm(helix.Height), pitch = Geo.Mm(helix.Pitch), turns = Geo.R(helix.Turns), hand = ArgumentNames.Snake(spec.Hand), taper = spec.Taper },
                warnings = Warnings(switched, status == "warning"),
            };
        }
        catch (ToolException)
        {
            // Create and Check have already deleted the feature; the profile copies remain.
            foreach (IncludedProfile copy in included)
            {
                DeleteIncluded(copy);
            }

            throw;
        }
    }

    private static object Loft(ModelDocument model, Model? existing, bool cut, object[] sections, int[] types, object[] origins)
    {
        const FeaturePropertyConstants none = FeaturePropertyConstants.igNone;
        const FeaturePropertyConstants side = FeaturePropertyConstants.igRight;
        if (cut)
        {
            return existing!.LoftedCutouts.AddSimple(sections.Length, sections, types, origins, side, none, none);
        }

        if (existing is null)
        {
            Model created = model.Models.AddLoftedProtrusion(sections.Length, sections, types, origins, null, side,
                none, 0, null, none, 0, null, none, 0, none, 0);
            return created.LoftedProtrusions.Item(created.LoftedProtrusions.Count);
        }

        return existing.LoftedProtrusions.AddSimple(sections.Length, sections, types, origins, side, none, none);
    }

    /// <summary>
    /// The trace curve of an edge-based path: one edge as it is; a path of several edges (a 3D sketch, a composite curve) as its
    /// Edges collection, because each item of the array is taken as a path of its own (three edges gave E_FAIL as three paths).
    /// </summary>
    private static object[] EdgePath(object curve)
    {
        Edge[] edges = CurveEdges(curve);
        return edges.Length == 1 ? [edges[0]] : [Late.Get(curve, "Edges", TopologyQuery.igQueryAll)!];
    }
    private static object Sweep(ModelDocument model, Model? existing, bool cut, object[] paths, int pathType, object[] sections, int[] types, object[] origins)
    {
        const FeaturePropertyConstants none = FeaturePropertyConstants.igNone;
        const FeaturePropertyConstants side = FeaturePropertyConstants.igRight;
        int[] pathTypes = Enumerable.Repeat(pathType, paths.Length).ToArray();
        if (cut)
        {
            return existing!.SweptCutouts.Add(paths.Length, paths, pathTypes, sections.Length, sections, types, origins, null, side, none, 0, null, none, 0, null);
        }

        if (existing is null)
        {
            Model created = model.Models.AddSweptProtrusion(paths.Length, paths, pathTypes, sections.Length, sections, types, origins, null, side,
                none, 0, null, none, 0, null);
            return created.SweptProtrusions.Item(created.SweptProtrusions.Count);
        }

        return existing.SweptProtrusions.Add(paths.Length, paths, pathTypes, sections.Length, sections, types, origins, null, side, none, 0, null, none, 0, null);
    }

    /// <summary>
    /// Helix from the start of the axis line (igStart) toward its end; the profile sets the radius. A taper (degrees) makes it
    /// conical. Verified: TaperSide igOutside widens it towards the end and igInside narrows it (a circle r2 at 10 mm from the
    /// axis, 40 high, 10°: 19.05 mm out at the end = 10 + 40 tan 10° + 2); igLeft and igRight give E_FAIL.
    /// </summary>
    private static object Helix(ModelDocument model, Model? existing, bool cut, RefAxis axis, object[] sections,
        (double Height, double Pitch, double Turns) size, FeaturePropertyConstants hand, double? taper)
    {
        const FeaturePropertyConstants start = FeaturePropertyConstants.igStart;
        const FeaturePropertyConstants side = FeaturePropertyConstants.igRight;
        Array profiles = sections;
        object angle = taper is { } t ? Units.DegToRad(Math.Abs(t)) : Type.Missing;
        object taperSide = taper is { } s ? (s >= 0 ? FeaturePropertyConstants.igOutside : FeaturePropertyConstants.igInside) : Type.Missing;
        if (cut)
        {
            return existing!.HelixCutouts.AddFinite(axis, start, 1, ref profiles, side, size.Height, size.Pitch, size.Turns, hand, angle, taperSide);
        }

        if (existing is null)
        {
            Model created = model.Models.AddFiniteBaseHelix(axis, start, 1, ref profiles, side, size.Height, size.Pitch, size.Turns, hand, angle, taperSide);
            return created.HelixProtrusions.Item(created.HelixProtrusions.Count);
        }

        return existing.HelixProtrusions.AddFinite(axis, start, 1, ref profiles, side, size.Height, size.Pitch, size.Turns, hand, angle, taperSide);
    }

    /// <summary>Height, pitch and turns (m, m, -) from two of them; if all three are given they must match.</summary>
    private static (double Height, double Pitch, double Turns) HelixSize(double? pitch, double? turns, double? height)
    {
        double? p = pitch is null ? null : Units.MmToM(Units.RequirePositive(pitch.Value, "pitch"));
        double? h = height is null ? null : Units.MmToM(Units.RequirePositive(height.Value, "height"));
        double? n = turns is null ? null : Units.RequirePositive(turns.Value, "turns");
        (double Height, double Pitch, double Turns) size = (p, n, h) switch
        {
            ({ } pp, { } nn, _) => (pp * nn, pp, nn),
            ({ } pp, null, { } hh) => (hh, pp, hh / pp),
            (null, { } nn, { } hh) => (hh, hh / nn, nn),
            _ => throw ToolException.InvalidArgument("A helix needs two of 'pitch' (mm), 'turns' and 'height' (mm)."),
        };
        if (h is { } given && Math.Abs(given - size.Height) > 1e-9)
        {
            throw ToolException.InvalidArgument($"'height' ({Units.MToMm(given)} mm) does not match pitch x turns ({Units.MToMm(size.Height)} mm).",
                "Give only two of pitch, turns and height.");
        }

        return size;
    }

    /// <summary>
    /// Start point of a section (2D sketch coordinates, m): the start of its first line or arc, or the
    /// point of a circle at +x. Solid Edge joins the start points of the sections; without them the loft gives E_FAIL.
    /// </summary>
    private static double[] StartPoint(Profile included, Profile sketch)
    {
        foreach (Profile profile in new[] { included, sketch })
        {
            if (profile.Lines2d.Count > 0)
            {
                profile.Lines2d.Item(1).GetStartPoint(out double x, out double y);
                return [x, y];
            }

            if (profile.Arcs2d.Count > 0)
            {
                profile.Arcs2d.Item(1).GetStartPoint(out double x, out double y);
                return [x, y];
            }

            if (profile.Circles2d.Count > 0)
            {
                Circle2d circle = profile.Circles2d.Item(1);
                circle.GetCenterPoint(out double x, out double y);
                return [x + circle.Radius, y];
            }
        }

        return [0.0, 0.0];
    }
}
