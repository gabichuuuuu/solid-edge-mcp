// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
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
/// Assembly features that add material or repeat features: extruded and revolved protrusions (each one an assembly body of
/// its own, verified: two protrusions gave two bodies, "Protrusion 1" of 4000 mm³ and "Protrusion 2" of 8000 mm³), patterns
/// of cutouts and holes, and welds of a weldment (fillet welds, whose bead is an assembly body, and label welds). Verified: a
/// 5 mm equal-leg fillet weld along a 100 mm joint leaves a bead of 1250 mm³; in a plain assembly both welds are refused
/// (E_INVALIDARG); a fillet weld whose faces do not meet throws 0x807B007D but leaves a failed weld (and its bead body) in
/// the collection, which is removed here.
/// </summary>
public static partial class AssemblyFeatureService
{
    /// <summary>Extruded protrusion of a closed sketch made in the assembly, <paramref name="depth"/> mm on one side or symmetric.</summary>
    public static object Protrusion(SeContext context, string? documentHandle, string? sketch, double? depth, string side, string? name)
    {
        (AssemblyDocument assembly, Profile profile) = AssemblySketch(context, documentHandle, sketch, "a closed sketch made in the assembly (se_sketch create with an assembly plane, then close)");
        double distance = Units.MmToM(Units.RequirePositive(depth ?? throw ToolException.InvalidArgument("'depth' is required: the protrusion length in mm."), "depth"));
        FeaturePropertyConstants extentSide = BodySide(side);
        CheckName(assembly, name);
        CheckUnused(assembly, profile);
        AssemblyFeaturesExtrudedProtrusions protrusions = FeaturesOf(assembly).ExtrudedProtrusions;
        object result = MakeBody(context, assembly, protrusions, name, "protrusion", () =>
        {
            Array profiles = new object[] { profile };
            FeaturePropertyConstants sideRef = extentSide;
            double length = distance;
            KeyPointExtentConstants flags = 0;
            return protrusions.Add(1, ref profiles, FeaturePropertyConstants.igFinite, ref sideRef, FeaturePropertyConstants.igLeft, ref length,
                null, ref flags, null, null);
        }, "Draw the sketch again, closed and without crossing lines.");
        Hide(profile);
        return result;
    }

    /// <summary>Revolved protrusion: a closed sketch made in the assembly turned about its axis by <paramref name="angle"/> degrees.</summary>
    public static object RevolvedProtrusion(SeContext context, string? documentHandle, string? sketch, string? axis, double? angle, string side, string? name)
    {
        (AssemblyDocument assembly, Profile profile) = AssemblySketch(context, documentHandle, sketch, "a closed sketch made in the assembly, with an axis (se_sketch 'axis' on a line, before 'close')");
        object refAxis = context.Handles.Resolve(axis ?? throw ToolException.InvalidArgument(
            "'axis' is required: the axis returned by se_sketch 'axis' on a line of the sketch, before 'close'."), "axis");
        double degrees = angle ?? 360;
        if (degrees is <= 0 or > 360)
        {
            throw ToolException.InvalidArgument("'angle' must be greater than 0 and at most 360 degrees.");
        }

        FeaturePropertyConstants extentSide = BodySide(side);
        CheckName(assembly, name);
        CheckUnused(assembly, profile);
        AssemblyFeaturesRevolvedProtrusions revolved = FeaturesOf(assembly).RevolvedProtrusions;
        object result = MakeBody(context, assembly, revolved, name, "revolved_protrusion", () =>
        {
            Array profiles = new object[] { profile };
            double radians = Units.DegToRad(degrees);
            KeyPointExtentConstants flags = 0;
            return revolved.Add(1, ref profiles, refAxis, FeaturePropertyConstants.igFinite, extentSide, FeaturePropertyConstants.igLeft, ref radians,
                null, ref flags, null, null);
        }, "Draw the sketch again, closed and not crossing the axis.");
        Hide(profile);
        return result;
    }

    /// <summary>
    /// Rectangular pattern of assembly cutouts and holes (by name): <paramref name="count"/> = [columns, rows] along the sketch
    /// axes of <paramref name="plane"/>, <paramref name="spacing"/> mm between them, rows turned by <paramref name="angle"/>
    /// degrees. The pattern sketch is made here with its origin at the first feature; the pattern is made with PatternType 1
    /// (verified: 0 leaves it failed). A pattern whose copies miss every part is failed (verified) and removed.
    /// </summary>
    public static object Pattern(SeContext context, string? documentHandle, string[]? featureNames, string? plane, int[]? count, double[]? spacing,
        double? angle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        if (featureNames is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'features' must list the names of the cutouts or holes to repeat (action 'list').");
        }

        if (string.IsNullOrWhiteSpace(plane))
        {
            throw ToolException.InvalidArgument("'plane' is required: the assembly plane '1'-'3' (or an assembly plane handle) whose sketch axes the rows follow.");
        }

        (int columns, int rows, double du, double dv) = Grid(count, spacing);
        object[] parents = featureNames.Select(n => Find(assembly, n)
            ?? throw ToolException.InvalidArgument($"The assembly has no feature '{n}'.", "List them with action 'list'.")).ToArray();
        if (parents.FirstOrDefault(p => p is not (AssemblyFeaturesExtrudedCutout or AssemblyFeaturesRevolvedCutout or AssemblyFeaturesHole)) is { } other)
        {
            throw ToolException.InvalidArgument($"'{NameOf(other)}' is not a cutout or a hole: only those are repeated by an assembly pattern.");
        }

        CheckName(assembly, name);
        object refPlane = SketchService.AssemblyPlane(context, assembly, plane);
        (double[] seedMin, double[] seedMax, int seedFaces) = FeatureBox(assembly, parents[0]);
        ProfileSet set = assembly.ProfileSets.Add();
        Profile profile = set.Profiles.Add(refPlane);
        double u = 0;
        double v = 0;
        if (seedFaces > 0)
        {
            profile.Convert3DCoordinate((seedMin[0] + seedMax[0]) / 2, (seedMin[1] + seedMax[1]) / 2, (seedMin[2] + seedMax[2]) / 2, out u, out v);
        }

        const double Unused = 0.01;
        profile.RectangularPatterns2d.Add(u, v, columns > 1 ? (columns - 1) * du : Unused, rows > 1 ? (rows - 1) * dv : Unused,
            Units.DegToRad(angle ?? 0), SolidEdgeFrameworkSupport.PatternOffsetTypeConstants.sePatternFixedOffset, columns, rows, columns > 1 ? du : Unused, rows > 1 ? dv : Unused);
        AssemblyFeaturesPatterns patterns = FeaturesOf(assembly).AssemblyFeaturesPatterns;
        int before = patterns.Count;
        AssemblyFeaturesPattern pattern;
        try
        {
            Array features = parents;
            pattern = patterns.Add(parents.Length, ref features, profile, (FeaturePropertyConstants)1);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            RemoveAddedSince(patterns, before);
            TryDelete(set);
            throw new ToolException("feature_failed", $"Solid Edge did not make the pattern: {ComErrors.Unwrap(ex).Message.Trim()}", "Check the counts, spacing and plane.");
        }

        string? status = StatusOf(pattern);
        int faces = FeatureFaces(pattern).Length;
        if (status is not ("ok" or "warning") || faces == 0)
        {
            Discard(pattern);
            TryDelete(set);
            throw new ToolException("feature_failed", $"Solid Edge did not make the pattern in the parts (its status is {status}, {faces} faces) and it was removed.",
                "Check that the copies fall on the parts the features cut: spacing in mm along the sketch axes of the plane (se_sketch create reports them as 'frame').");
        }

        if (name is not null)
        {
            pattern.Name = name;
        }

        Hide(profile);
        var warnings = new List<string>();
        int copies = (columns * rows) - 1;
        if (seedFaces > 0 && faces < copies * seedFaces)
        {
            warnings.Add($"The pattern left {faces} faces for {copies} copies of a feature with {seedFaces}: some copies may miss the parts.");
        }

        if (status == "warning")
        {
            warnings.Add("Solid Edge reports a warning on this feature.");
        }

        object described = Describe(context, assembly, pattern, "pattern");
        return warnings.Count == 0 ? described : new { feature = described, warnings };
    }

    /// <summary>
    /// Fillet weld of a weldment between base faces and target faces of its parts (se_geometry 'faces' with source = the
    /// occurrence), with legs in mm (<paramref name="leg2"/> on the target faces, default the same). Checked by its bead.
    /// </summary>
    public static object FilletWeld(SeContext context, string? documentHandle, string[]? baseFaces, string[]? targetFaces, double? leg, double? leg2, string? name)
    {
        AssemblyDocument assembly = Weldment(context, documentHandle);
        object[] bases = References(context, assembly, baseFaces, "faces", "base faces");
        object[] targets = References(context, assembly, targetFaces, "targetFaces", "target faces");
        double baseLeg = Units.RequirePositive(leg ?? throw ToolException.InvalidArgument("'leg' is required: the weld leg in mm on the base faces."), "leg");
        double targetLeg = Units.RequirePositive(leg2 ?? baseLeg, "leg2");
        CheckName(assembly, name);
        LabelWeldData data = assembly.LabelWeldDataCollection.Add();
        AssemblyFilletWelds welds = FeaturesOf(assembly).FilletWelds;
        return MakeBody(context, assembly, welds, name, "fillet_weld", () =>
        {
            Array baseArray = bases;
            Array targetArray = targets;
            return welds.Add(bases.Length, ref baseArray, targets.Length, ref targetArray, data,
                Math.Abs(baseLeg - targetLeg) < 1e-9 ? FilletWeldSetbackConstants.seFilletWeldEqualSetback : FilletWeldSetbackConstants.seFilletWeldUnequalSetback,
                Units.MmToM(baseLeg), Units.MmToM(targetLeg));
        }, "The base and target faces must meet along an edge (e.g. the top face of a plate and the side face of a plate standing on it).");
    }

    /// <summary>Label weld of a weldment on edges of its parts (se_geometry 'edges' of an occurrence face): marks them as welded, with no bead.</summary>
    public static object LabelWeld(SeContext context, string? documentHandle, string[]? edgeHandles, string? name)
    {
        AssemblyDocument assembly = Weldment(context, documentHandle);
        object[] edges = References(context, assembly, edgeHandles, "edges", "edges");
        CheckName(assembly, name);
        LabelWeldData data = assembly.LabelWeldDataCollection.Add();
        AssemblyLabelWelds labels = FeaturesOf(assembly).LabelWelds;
        int before = labels.Count;
        AssemblyLabelWeld label;
        try
        {
            Array edgeArray = edges;
            label = labels.Add(edges.Length, ref edgeArray, data);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            RemoveAddedSince(labels, before);
            throw new ToolException("feature_failed", $"Solid Edge did not make the label weld: {ComErrors.Unwrap(ex).Message.Trim()}", "Pass edges of the parts read with se_geometry 'edges'.");
        }

        string? status = StatusOf(label);
        if (status is not ("ok" or "warning"))
        {
            TryDeleteFeature(label);
            throw new ToolException("feature_failed", $"Solid Edge did not make the label weld (its status is {status}) and it was removed.", "Pass edges of the parts read with se_geometry 'edges'.");
        }

        if (name is not null)
        {
            label.Name = name;
        }

        return Describe(context, assembly, label, "label_weld");
    }

    /// <summary>
    /// AssemblyFeatures objects read before a structural frame was added. Verified: once an assembly gets a frame while it
    /// has no assembly feature, <c>AssemblyDocument.AssemblyFeatures</c> fails with E_FAIL for good (also after leaving the
    /// Frame environment, saving and reopening); an object read before the frame still makes holes and welds between parts,
    /// but not welds on the frame members. An assembly feature made before the frame, even one deleted again, keeps
    /// everything working (r219-r227).
    /// </summary>
    private static readonly ConditionalWeakTable<AssemblyDocument, AssemblyFeatures> KeptFeatures = new();

    /// <summary>
    /// Prepares an assembly for a structural frame (se_frame): keeps its AssemblyFeatures object and, when it has no assembly
    /// feature yet, makes a 1 mm³ protrusion and deletes it again with its sketch, so that the property and welds on the
    /// members keep working after the frame. The document ends as it was.
    /// </summary>
    internal static void PrepareForFrame(AssemblyDocument assembly)
    {
        AssemblyFeatures features;
        try
        {
            features = assembly.AssemblyFeatures;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            // A frame came first (made before or outside this server): nothing can bring the property back.
            return;
        }

        KeptFeatures.AddOrUpdate(assembly, features);
        if (Features(assembly).Any())
        {
            return;
        }

        ProfileSet set = assembly.ProfileSets.Add();
        try
        {
            Profile profile = set.Profiles.Add(assembly.AsmRefPlanes.Item(1));
            profile.Circles2d.AddByCenterRadius(0, 0, 0.0005);
            profile.End(ProfileValidationType.igProfileClosed);
            Array profiles = new object[] { profile };
            FeaturePropertyConstants side = FeaturePropertyConstants.igRight;
            double length = 0.001;
            KeyPointExtentConstants flags = 0;
            object protrusion = features.ExtrudedProtrusions.Add(1, ref profiles, FeaturePropertyConstants.igFinite, ref side,
                FeaturePropertyConstants.igLeft, ref length, null, ref flags, null, null);
            Discard(protrusion);
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            // Without it the frame is still made; only later assembly features may be refused.
        }
        finally
        {
            TryDelete(set);
        }
    }

    /// <summary>Whether Solid Edge gives the assembly's AssemblyFeatures (false after a frame made before any assembly feature).</summary>
    internal static bool FeaturesAvailable(AssemblyDocument assembly)
    {
        try
        {
            _ = assembly.AssemblyFeatures;
            return true;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            return false;
        }
    }

    private static AssemblyFeatures FeaturesOf(AssemblyDocument assembly)
    {
        try
        {
            return assembly.AssemblyFeatures;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80004005))
        {
            return KeptFeatures.TryGetValue(assembly, out AssemblyFeatures? kept)
                ? kept
                : throw new ToolException("solid_edge_limitation",
                    "Solid Edge does not give the assembly features of this assembly: it stops doing so for good when a structural frame is added to an assembly without assembly features (frames made with se_frame avoid it).",
                    "Run se_frame repair: it makes the frames again so that assembly features work.");
        }
    }

    private static (AssemblyDocument Assembly, Profile Profile) AssemblySketch(SeContext context, string? documentHandle, string? sketch, string what)
    {
        if (string.IsNullOrWhiteSpace(sketch))
        {
            throw ToolException.InvalidArgument($"'sketch' is required: {what}.");
        }

        Profile profile = SketchService.Resolve(context, sketch);
        SolidEdgeDocument document = documentHandle is null ? context.DocumentOf(sketch) : context.GetDocument(documentHandle);
        return document is AssemblyDocument assembly
            ? (assembly, profile)
            : throw new ToolException("wrong_document_type", "The sketch is not in an assembly.", "Draw it with se_sketch create in the assembly (plane '1'-'3' of the assembly).");
    }

    private static AssemblyDocument Weldment(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        return assembly.WeldmentAssembly
            ? assembly
            : throw new ToolException("wrong_document_type", "Welds are made in a weldment: Solid Edge refuses them in a plain assembly.",
                "Create one with se_documents new kind 'weldment' and insert the parts to weld.");
    }

    /// <summary>Faces or edges of the assembly's parts as references in the assembly, the objects the weld calls expect.</summary>
    private static object[] References(SeContext context, AssemblyDocument assembly, string[]? handles, string argument, string what)
    {
        if (handles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument($"'{argument}' must list the {what}: handles from se_geometry with 'source' = an occurrence (or a face of one).");
        }

        return handles.Select(h => context.Handles.Resolve(h, argument) is OccurrenceElement element
            ? element.Reference(assembly)
            : throw new ToolException("handle_wrong_type", $"'{h}' is not a face or edge read from an occurrence.",
                "Read them with se_geometry 'faces' or 'edges' and 'source' = the occurrence handle.")).ToArray();
    }

    private static FeaturePropertyConstants BodySide(string side) => side switch
    {
        "auto" or "normal" => FeaturePropertyConstants.igRight,
        "reverse" => FeaturePropertyConstants.igLeft,
        "symmetric" => FeaturePropertyConstants.igSymmetric,
        _ => throw ToolException.InvalidArgument($"Unknown side '{side}'.", "Use normal, reverse or symmetric."),
    };

    private static (int Columns, int Rows, double Du, double Dv) Grid(int[]? count, double[]? spacing)
    {
        if (count is not { Length: 1 or 2 } || count.Any(c => c < 1))
        {
            throw ToolException.InvalidArgument("'count' must be [columns] or [columns, rows], each 1 or more (the original included).");
        }

        int columns = count[0];
        int rows = count.Length > 1 ? count[1] : 1;
        if (columns * rows < 2)
        {
            throw ToolException.InvalidArgument("'count' must give at least 2 instances (the original included).");
        }

        double Space(int index, int instances)
        {
            if (instances < 2)
            {
                return 0;
            }

            return spacing is { } s && s.Length > index
                ? Units.MmToM(Units.RequirePositive(s[index], "spacing"))
                : throw ToolException.InvalidArgument("'spacing' must give the distance in mm between columns (and between rows when rows > 1).");
        }

        return (columns, rows, Space(0, columns), Space(1, rows));
    }

    /// <summary>
    /// Creates a feature that adds a body (protrusion or weld bead) and checks its status and its body's volume. A failed call
    /// may still leave a feature in the collection (verified on fillet welds), so every feature added since is removed.
    /// </summary>
    private static object MakeBody(SeContext context, AssemblyDocument assembly, object collection, string? name, string type, Func<object> add, string hint)
    {
        int before = Late.Count(collection);
        object feature;
        try
        {
            feature = add();
        }
        catch (Exception ex) when (ComErrors.Unwrap(ex) is COMException or ArgumentException && !ComErrors.IsGone(ex))
        {
            RemoveAddedSince(collection, before);
            throw new ToolException("feature_failed", $"Solid Edge did not make the {type.Replace('_', ' ')}: {ComErrors.Unwrap(ex).Message.Trim()}", hint);
        }

        string? status = StatusOf(feature);
        double volume = BodyVolume(feature);
        // A busy Solid Edge can report the feature ok before its body exists (seen once in a long acceptance run): let it
        // finish its pending work before deciding.
        for (int i = 0; i < 5 && status is ("ok" or "warning") && volume <= 0; i++)
        {
            assembly.Application.DoIdle();
            System.Threading.Thread.Sleep(200);
            volume = BodyVolume(feature);
        }

        if (status is not ("ok" or "warning") || volume <= 0)
        {
            Discard(feature);
            throw new ToolException("feature_failed", $"Solid Edge did not make the {type.Replace('_', ' ')} (its status is {status}); it was removed{(type.EndsWith("weld", StringComparison.Ordinal) ? "" : " with its sketch")}.", hint);
        }

        if (name is not null)
        {
            Late.Set(feature, "Name", name);
        }

        object described = Describe(context, assembly, feature, type);
        return status == "ok" ? described : new { feature = described, warnings = new[] { "Solid Edge reports a warning on this feature." } };
    }

    private static void RemoveAddedSince(object collection, int before)
    {
        try
        {
            for (int i = Late.Count(collection); i > before; i--)
            {
                if (Late.Call(collection, "Item", i) is { } added)
                {
                    Discard(added);
                }
            }
        }
        catch (Exception ex) when (Late.IsMemberFailure(ex))
        {
            // The error being reported is the original one.
        }
    }

    /// <summary>Volume of the assembly body a protrusion or fillet weld makes (m³); 0 if it has none.</summary>
    private static double BodyVolume(object feature)
    {
        try
        {
            return Late.TryGet(feature, "AssemblyBody") is { } assemblyBody && Late.TryGet(assemblyBody, "Body") is SolidEdgeGeometry.Body body ? body.Volume : 0;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return 0;
        }
    }

    /// <summary>Range of a protrusion or fillet weld in the assembly (m), or null.</summary>
    private static double[]? FeatureRange(object feature)
    {
        try
        {
            double x1, y1, z1, x2, y2, z2;
            switch (feature)
            {
                case AssemblyFeaturesExtrudedProtrusion extruded:
                    extruded.Range(out x1, out y1, out z1, out x2, out y2, out z2);
                    break;
                case AssemblyFeaturesRevolvedProtrusion revolved:
                    revolved.Range(out x1, out y1, out z1, out x2, out y2, out z2);
                    break;
                case AssemblyFilletWeld weld:
                    weld.Range(out x1, out y1, out z1, out x2, out y2, out z2);
                    break;
                default:
                    return null;
            }

            return [x1, y1, z1, x2, y2, z2];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>Faces of a pattern as a whole (Solid Edge gives them in its first part).</summary>
    private static SolidEdgeGeometry.Face[] FeatureFaces(AssemblyFeaturesPattern pattern)
    {
        try
        {
            return [.. Late.Items((Faces)pattern.Faces[TopologyQuery.igQueryAll]).OfType<SolidEdgeGeometry.Face>()];
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    /// <summary>
    /// Box (m, assembly coordinates) of the faces a cutout or hole leaves in its first part, and their number; or of the
    /// faces of a pattern, which are in the first part of the features it repeats.
    /// </summary>
    private static (double[] Min, double[] Max, int Faces) FeatureBox(AssemblyDocument assembly, object feature)
    {
        object source = feature is AssemblyFeaturesPattern pattern ? ParentFeatures(pattern).FirstOrDefault() ?? feature : feature;
        Occurrence? first = ScopeParts(assembly, source).FirstOrDefault();
        if (first is null)
        {
            return ([], [], 0);
        }

        SolidEdgeGeometry.Face[] faces = feature is AssemblyFeaturesPattern repeated ? FeatureFaces(repeated) : FacesOn(feature, first);
        if (faces.Length == 0)
        {
            return ([], [], 0);
        }

        Placement placement = Placement.Of(first);
        double[] lo = [double.MaxValue, double.MaxValue, double.MaxValue];
        double[] hi = [double.MinValue, double.MinValue, double.MinValue];
        foreach (SolidEdgeGeometry.Face face in faces)
        {
            Array min = new double[3];
            Array max = new double[3];
            face.GetRange(ref min, ref max);
            (double[] a, double[] b) = placement.Box(Geo.Doubles(min).ToArray(), Geo.Doubles(max).ToArray());
            for (int i = 0; i < 3; i++)
            {
                lo[i] = Math.Min(lo[i], a[i]);
                hi[i] = Math.Max(hi[i], b[i]);
            }
        }

        return (lo, hi, faces.Length);
    }

    private static object[] ParentFeatures(AssemblyFeaturesPattern pattern) =>
        OutArray(pattern, typeof(AssemblyFeaturesPattern), nameof(AssemblyFeaturesPattern.GetParentFeatures));

    private static object Box(double[] min, double[] max) => new { min = min.Select(Geo.Mm).ToArray(), max = max.Select(Geo.Mm).ToArray() };
}
