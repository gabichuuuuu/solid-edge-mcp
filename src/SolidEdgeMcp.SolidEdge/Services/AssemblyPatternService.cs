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
/// "Duplicate" command does. Solid Edge 2026 does not create component mirrors through the API (<c>AssemblyMirrors</c> has
/// no Add) and rejects every curve tried for patterns along a curve (<c>CreateAlongCurveEx3</c>: E_NOINTERFACE with part
/// edges, their references and lines of an assembly layout).
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
            type = pattern.PatternType() == AssemblyPatternTypeConstants.seAssemblyDuplicatePatternType ? "duplicate" : "part_pattern",
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
