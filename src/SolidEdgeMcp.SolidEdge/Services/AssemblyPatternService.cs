// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;
using static SolidEdgeMcp.SolidEdge.Services.AssemblyService;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Component patterns that follow a pattern of a part (e.g. a pin in the first hole repeated on every hole of the plate's
/// hole pattern), as the "Pattern" command of the assembly does with "part pattern", and duplicate patterns, as the
/// "Duplicate" command does, and patterns along a curve of an assembly layout. Solid Edge 2026 does not create component
/// mirrors through the API (<c>AssemblyMirrors</c> has no Add).
/// </summary>
public static class AssemblyPatternService
{
    public static object List(SeContext context, string? documentHandle)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        object[] patterns = Items<AssemblyPattern>(assembly.AssemblyPatterns).Select(p => Describe(context, assembly, p)).ToArray();
        return new { document = context.DocumentHandle((SolidEdgeDocument)assembly), count = patterns.Length, patterns };
    }

    /// <summary>
    /// Repeats <paramref name="occurrenceHandles"/> on every instance of a pattern of the part of <paramref name="sourceHandle"/>.
    /// The occurrences are placed relative to the reference feature (by default the first feature the pattern repeats).
    /// </summary>
    public static object Create(SeContext context, string[]? occurrenceHandles, string? sourceHandle, string? patternName, string? referenceName, string? name)
    {
        if (occurrenceHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'occurrences' must list the occurrences to repeat (e.g. the pin placed in the first hole).");
        }

        Occurrence[] masters = occurrenceHandles.Select(h => Occurrence(context, h, "occurrences")).ToArray();
        Occurrence source = Occurrence(context, sourceHandle, "source");
        AssemblyDocument assembly = source.TopLevelDocument;
        ModelDocument part = PartOf(source);
        Model model = part.RequireModel();
        Pattern[] partPatterns = Items<Pattern>(model.Patterns).ToArray();
        string available = partPatterns.Length == 0 ? "none" : string.Join(", ", partPatterns.Select(p => p.Name));
        Pattern pattern = patternName is null
            ? partPatterns.Length == 1 ? partPatterns[0] : throw ToolException.InvalidArgument(
                $"'{source.Name}' has {partPatterns.Length} patterns ({available}); pass 'pattern' with the one to follow.")
            : partPatterns.FirstOrDefault(p => string.Equals(p.Name, patternName, StringComparison.OrdinalIgnoreCase))
                ?? throw ToolException.InvalidArgument($"'{source.Name}' has no pattern '{patternName}'.", $"Patterns of the part: {available}.");
        object reference = referenceName is null ? FirstInput(pattern) : PartFeature(part, referenceName);

        string patternTitle = name ?? NextName(assembly);
        if (Find(assembly, patternTitle) is not null)
        {
            throw ToolException.InvalidArgument($"The assembly already has a pattern named '{patternTitle}'.", "Pass another 'name'.");
        }

        int before = assembly.Occurrences.Count;
        Array mastersArray = masters.Cast<object>().ToArray();
        AssemblyPattern created;
        try
        {
            created = assembly.AssemblyPatterns.Create(patternTitle, ref mastersArray,
                assembly.CreateReference(source, pattern), assembly.CreateReference(source, reference));
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("pattern_failed", $"Solid Edge did not create the pattern ({ex.Message}).",
                "Check that the occurrences are placed at the reference feature (the first instance of the part pattern).");
        }

        int added = assembly.Occurrences.Count - before;
        if (added <= 0)
        {
            TryDelete(assembly, patternTitle);
            throw new ToolException("pattern_failed", "The pattern added no occurrences and was removed.",
                "Check that the part pattern has more than one instance and that the occurrences are placed at the reference feature.");
        }

        return new { pattern = Describe(context, assembly, created), added, occurrences = assembly.Occurrences.Count };
    }

    /// <summary>
    /// Copies <paramref name="occurrenceHandles"/>, placed relative to the occurrence of <paramref name="fromHandle"/>, onto each
    /// occurrence of <paramref name="targetHandles"/> at the same relative position and orientation (e.g. the bolts of one flange
    /// repeated on the other flanges). Checked by the number of copies and their positions.
    /// </summary>
    public static object Duplicate(SeContext context, string[]? occurrenceHandles, string? fromHandle, string[]? targetHandles, string? name)
    {
        if (occurrenceHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'occurrences' must list the occurrences to duplicate (e.g. the bolt placed on the first flange).");
        }

        if (targetHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'targets' must list the occurrences to copy them onto (e.g. the other flanges).");
        }

        Occurrence[] sources = occurrenceHandles.Select(h => Occurrence(context, h, "occurrences")).ToArray();
        Occurrence from = Occurrence(context, fromHandle, "source");
        Occurrence[] targets = targetHandles.Select(h => Occurrence(context, h, "targets")).ToArray();
        int[] ids = [.. sources.Select(o => o.OccurrenceID), from.OccurrenceID, .. targets.Select(o => o.OccurrenceID)];
        if (ids.Distinct().Count() != ids.Length)
        {
            throw ToolException.InvalidArgument("'occurrences', 'source' and 'targets' must be different occurrences, each listed once.",
                "'source' is the occurrence the duplicated ones sit on; 'targets' are the others.");
        }

        AssemblyDocument assembly = from.TopLevelDocument;
        string patternTitle = name ?? NextName(assembly);
        if (Find(assembly, patternTitle) is not null)
        {
            throw ToolException.InvalidArgument($"The assembly already has a pattern named '{patternTitle}'.", "Pass another 'name'.");
        }

        // Each copy keeps the position of its source relative to 'source', seen from each target.
        Placement reference = Placement.Of(from);
        double[][] expected = targets.SelectMany(t =>
        {
            Placement target = Placement.Of(t);
            return sources.Select(s => target.Point(reference.Local(Placement.Of(s).Origin)));
        }).ToArray();

        int before = assembly.Occurrences.Count;
        Array sourcesArray = sources.Cast<object>().ToArray();
        Array targetsArray = targets.Cast<object>().ToArray();
        AssemblyPattern created;
        try
        {
            created = assembly.AssemblyPatterns.CreateDuplicateEx(patternTitle, sources.Length, ref sourcesArray, from, targets.Length, ref targetsArray);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("pattern_failed", $"Solid Edge did not create the duplicate pattern ({ex.Message}).",
                "Check that the occurrences to duplicate are not part of another pattern.");
        }

        int added = assembly.Occurrences.Count - before;
        Occurrence[] copies = Copies(created);
        bool placed = expected.All(e => copies.Any(c => Geo.Length([.. Placement.Of(c).Origin.Zip(e, (a, b) => a - b)]) < 1e-6));
        if (added != expected.Length || !placed)
        {
            TryDelete(assembly, patternTitle);
            throw new ToolException("pattern_failed",
                $"The duplicate pattern added {added} occurrences ({expected.Length} expected){(placed ? "" : " away from their expected positions")} and was removed.",
                "Check the occurrences with se_assembly 'list'.");
        }

        return new { pattern = Describe(context, assembly, created), added, occurrences = assembly.Occurrences.Count };
    }

    /// <summary>
    /// Repeats <paramref name="occurrenceHandles"/> along an element of an assembly layout, <paramref name="count"/> in all
    /// (the originals included) every <paramref name="spacing"/> mm measured along the curve from its start (or its end with
    /// <paramref name="reverse"/>). The copies are moved, not turned. Verified: <c>CreateAlongCurveEx3</c> takes the curve of
    /// the layout profile's curve body (<c>MapGeometryToCurves</c>) and one of its <c>CurveVertices</c> as the anchor; part
    /// edges, their references, the 2D element itself or a vertex reference give E_NOINTERFACE, E_INVALIDARG or 0x80040224. Only
    /// the fixed spacing is used: the fit spacing closed Solid Edge 2026 (RPC server gone).
    /// </summary>
    public static object AlongCurve(SeContext context, string[]? occurrenceHandles, string? curveHandle, int? count, double? spacing, bool reverse, string? name)
    {
        if (occurrenceHandles is not { Length: > 0 })
        {
            throw ToolException.InvalidArgument("'occurrences' must list the occurrences to repeat along the curve.");
        }

        if (string.IsNullOrWhiteSpace(curveHandle))
        {
            throw ToolException.InvalidArgument("'curve' is required: an element (line, arc, spline) of an assembly layout.",
                "Draw it with se_sketch create layout=true, a line or arc, then se_sketch close (open=true for an open path).");
        }

        int copies = count ?? throw ToolException.InvalidArgument("'count' is required: occurrences in all along the curve, the originals included.");
        if (copies < 2)
        {
            throw ToolException.InvalidArgument("'count' must be at least 2 (the original and one copy).");
        }

        double step = Units.MmToM(Units.RequirePositive(spacing ?? throw ToolException.InvalidArgument("'spacing' (mm along the curve) is required."), "spacing"));
        Occurrence[] masters = occurrenceHandles.Select(h => Occurrence(context, h, "occurrences")).ToArray();
        AssemblyDocument assembly = masters[0].TopLevelDocument;
        object element = context.Handles.Resolve(curveHandle, "curve");
        Profile profile = Late.Get(element, "Parent") as Profile ?? throw ToolException.InvalidArgument(
            $"'{curveHandle}' is not an element of a sketch or layout.", "Pass a line, arc or spline of an assembly layout (se_sketch create layout=true).");
        SolidEdgeGeometry.Curve curve = LayoutCurve(profile, element, curveHandle);

        Array start = new double[3];
        Array end = new double[3];
        curve.GetEndPoints(ref start, ref end);
        double[] anchorPoint = Geo.Doubles(reverse ? end : start).ToArray();
        SolidEdgeGeometry.CurveVertex anchor = Items<SolidEdgeGeometry.CurveVertex>(curve.CurveVertices)
            .FirstOrDefault(v => Geo.Length([.. VertexPoint(v).Zip(anchorPoint, (a, b) => a - b)]) < 1e-9)
            ?? throw ToolException.InvalidArgument($"'{curveHandle}' is a closed curve; use an open line, arc or spline.");
        curve.GetParamExtents(out double p0, out double p1);
        curve.GetLengthAtParam(p0, p1, out double length);
        if ((copies - 1) * step > length + 1e-9)
        {
            throw ToolException.InvalidArgument(
                $"{copies} occurrences {Geo.Mm(step)} mm apart need {Geo.Mm((copies - 1) * step)} mm, but the curve is {Geo.Mm(length)} mm long.",
                "Use fewer occurrences, a smaller spacing or a longer curve.");
        }

        // Each copy is the original moved by the chord from the anchor to the point at i·spacing along the curve.
        double[][] offsets = Enumerable.Range(1, copies - 1).Select(i =>
        {
            double along = reverse ? length - i * step : i * step;
            Array parameters = new[] { ParamAtLength(curve, p0, p1, along) };
            Array point = new double[3];
            curve.GetPointAtParam(1, ref parameters, ref point);
            return Geo.Doubles(point).Zip(anchorPoint, (a, b) => a - b).ToArray();
        }).ToArray();
        double[][] expected = masters.SelectMany(m => offsets.Select(o => Placement.Of(m).Origin.Zip(o, (a, b) => a + b).ToArray())).ToArray();

        string patternTitle = name ?? NextName(assembly);
        if (Find(assembly, patternTitle) is not null)
        {
            throw ToolException.InvalidArgument($"The assembly already has a pattern named '{patternTitle}'.", "Pass another 'name'.");
        }

        int before = assembly.Occurrences.Count;
        HashSet<int> existing = [.. Items<Occurrence>(assembly.Occurrences).Select(o => o.OccurrenceID)];
        Array mastersArray = masters.Cast<object>().ToArray();
        Array curves = new object[] { curve };
        Array none = Array.Empty<object>();
        AssemblyPattern created;
        try
        {
            created = assembly.AssemblyPatterns.CreateAlongCurveEx3(masters.Length, ref mastersArray, 1, ref curves, anchor,
                PatternCurveAnchorSideConstants.sePatternCurveRightSide, 0, SolidEdgeAssembly.PatternOffsetTypeConstants.sePatternFixedOffset, copies, step,
                0, ref none, null, PatternCurveAnchorSideConstants.sePatternCurveLeftSide, 0, SolidEdgeAssembly.PatternOffsetTypeConstants.sePatternFixedOffset, 1, 0,
                PatternTransformTypeConstants.sePatternTransformLinear, PatternTransformRotateTypeConstants.sePatternTransformRotateOnCurvePosition,
                null, patternTitle, 0);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("pattern_failed", $"Solid Edge did not create the pattern along the curve ({ex.Message}).",
                "Check that the curve is an open element of a closed layout (se_sketch close open=true) and that the occurrences are not in another pattern.");
        }

        int added = assembly.Occurrences.Count - before;
        double[][] origins = [.. Items<Occurrence>(assembly.Occurrences).Where(o => !existing.Contains(o.OccurrenceID)).Select(o => Placement.Of(o).Origin)];
        bool placed = expected.All(e => origins.Any(o => Geo.Length([.. o.Zip(e, (a, b) => a - b)]) < 1e-6));
        if (added != expected.Length || !placed)
        {
            TryDelete(assembly, patternTitle);
            string Points(IEnumerable<double[]> points) => string.Join(" ", points.Select(p => $"[{string.Join(", ", p.Select(Geo.Mm))}]"));
            throw new ToolException("pattern_failed",
                $"The pattern along the curve added {added} occurrences ({expected.Length} expected){(placed ? "" : $" at {Points(origins)} instead of {Points(expected)}")} and was removed.",
                "Check the curve and the spacing; se_assembly 'list' shows the occurrences.");
        }

        return new { pattern = Describe(context, assembly, created), added, length = Geo.Mm(length), occurrences = assembly.Occurrences.Count };
    }

    /// <summary>The curve of the layout profile's curve body that a 2D element maps to (the profile must be closed: se_sketch close).</summary>
    private static SolidEdgeGeometry.Curve LayoutCurve(Profile profile, object element, string handle)
    {
        if (LayoutOf(profile) is null)
        {
            throw ToolException.InvalidArgument($"'{handle}' is not on an assembly layout.", "Draw the curve on a layout: se_sketch create layout=true.");
        }

        object? mapped = null;
        int number;
        try
        {
            profile.MapGeometryToCurves(element, out number, out mapped);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("pattern_failed", $"Solid Edge did not give the curve of '{handle}' ({ex.Message}).",
                "Close the layout first: se_sketch close (open=true for an open path).", ex);
        }

        return (mapped as Array)?.OfType<SolidEdgeGeometry.Curve>().ToArray() is [SolidEdgeGeometry.Curve single] && number == 1
            ? single
            : throw new ToolException("pattern_failed", $"'{handle}' maps to {number} curves of the layout; one is needed.",
                "Close the layout first (se_sketch close open=true) and pass a single line, arc or spline.");
    }

    /// <summary>
    /// Parameter at an arc length from the start, by bisection: on layout curves <c>GetParamAtLength</c> returns the start
    /// parameter whatever the length (verified), while <c>GetLengthAtParam</c> works (arcs are parametrized by angle).
    /// </summary>
    private static double ParamAtLength(SolidEdgeGeometry.Curve curve, double p0, double p1, double target)
    {
        double low = p0;
        double high = p1;
        for (int i = 0; i < 60; i++)
        {
            double mid = (low + high) / 2;
            curve.GetLengthAtParam(p0, mid, out double length);
            (low, high) = length < target ? (mid, high) : (low, mid);
        }

        return (low + high) / 2;
    }

    private static Layout? LayoutOf(Profile profile) =>
        Late.Get(profile.Parent, "Parent") is AssemblyDocument assembly
            ? Items<Layout>(assembly.Layouts).FirstOrDefault(l => ReferenceEquals(l.Profile, profile))
            : null;

    private static double[] VertexPoint(SolidEdgeGeometry.CurveVertex vertex)
    {
        Array point = new double[3];
        vertex.GetPointData(ref point);
        return Geo.Doubles(point).ToArray();
    }

    /// <summary>Deletes the pattern and the occurrences it created (the original occurrences stay).</summary>
    public static object Delete(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        AssemblyPattern pattern = Require(assembly, name);
        Occurrence[] copies = Copies(pattern);
        int before = assembly.Occurrences.Count;
        assembly.AssemblyPatterns.Delete(pattern.Name);
        foreach (Occurrence copy in copies)
        {
            if (context.Handles.TryGetId(copy, out string? id) && id is not null)
            {
                context.Handles.Remove(id, "its pattern was deleted");
            }
        }

        return Find(assembly, name!) is null
            ? new { deleted = name, removed = before - assembly.Occurrences.Count, occurrences = assembly.Occurrences.Count }
            : throw new ToolException("pattern_failed", $"Solid Edge did not delete the pattern '{name}'.", "Check it with action 'list'.");
    }

    /// <summary>Dissolves the pattern: its occurrences become ordinary ones and stay where they are.</summary>
    public static object Dissolve(SeContext context, string? documentHandle, string? name)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        AssemblyPattern pattern = Require(assembly, name);
        int before = assembly.Occurrences.Count;
        assembly.AssemblyPatterns.Drop(pattern.Name);
        return Find(assembly, name!) is null && assembly.Occurrences.Count == before
            ? new { dissolved = name, occurrences = assembly.Occurrences.Count }
            : throw new ToolException("pattern_failed", $"Solid Edge did not dissolve the pattern '{name}' as expected.", "Check the assembly with se_assembly 'list'.");
    }

    private static object Describe(SeContext context, AssemblyDocument assembly, AssemblyPattern pattern)
    {
        HashSet<int> grounded = GroundedIds(assembly);
        return new
        {
            name = pattern.Name,
            type = pattern.PatternType() switch
            {
                AssemblyPatternTypeConstants.seAssemblyDuplicatePatternType => "duplicate",
                AssemblyPatternTypeConstants.seAssemblyPatternAlongCurveType => "curve",
                _ => "part_pattern",
            },
            instances = pattern.Count,
            visible = pattern.Visible,
            occurrences = Copies(pattern).Select(o => AssemblyService.Describe(context, assembly, o, grounded)).ToArray(),
        };
    }

    /// <summary>Occurrences created by the pattern (not the originals).</summary>
    private static Occurrence[] Copies(AssemblyPattern pattern)
    {
        try
        {
            Array occurrences = Array.Empty<object>();
            pattern.GetOccurrences(ref occurrences);
            return occurrences.OfType<Occurrence>().ToArray();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return [];
        }
    }

    private static object FirstInput(Pattern pattern)
    {
        Array inputs = Array.Empty<object>();
        pattern.GetInputFeatures(ref inputs);
        return inputs.OfType<object>().FirstOrDefault()
            ?? throw new ToolException("pattern_failed", $"The pattern '{pattern.Name}' has no input feature.", "Pass 'reference' with the feature name.");
    }

    private static object PartFeature(ModelDocument part, string name) =>
        Late.Items(part.EdgebarFeatures).FirstOrDefault(f => string.Equals(Late.TryGetString(f, "Name"), name, StringComparison.OrdinalIgnoreCase))
        ?? throw ToolException.InvalidArgument($"The part has no feature '{name}'.", "Use the name of the feature the pattern repeats (e.g. 'Hole_1').");

    private static AssemblyPattern? Find(AssemblyDocument assembly, string name) =>
        Items<AssemblyPattern>(assembly.AssemblyPatterns).FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    private static AssemblyPattern Require(AssemblyDocument assembly, string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? throw ToolException.InvalidArgument("'name' is required: the pattern name (action 'list').")
            : Find(assembly, name) ?? throw ToolException.InvalidArgument($"The assembly has no pattern '{name}'.", "List them with action 'list'.");

    private static string NextName(AssemblyDocument assembly)
    {
        int n = assembly.AssemblyPatterns.Count + 1;
        while (Find(assembly, $"Pattern {n}") is not null)
        {
            n++;
        }

        return $"Pattern {n}";
    }

    private static void TryDelete(AssemblyDocument assembly, string name)
    {
        try
        {
            assembly.AssemblyPatterns.Delete(name);
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            // The error being reported is the original one.
        }
    }
}
