// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Arguments;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// The reports of the Reports and Assembly Statistics dialog boxes, read from the occurrence tree: the indented bill of
/// materials (each part once per subassembly), the exploded one (each occurrence) and the counts of parts and subassemblies,
/// in total and unique, with the unique files. The dialogs themselves have no object in the type libraries.
/// </summary>
public static partial class AssemblyService
{
    public static object Report(SeContext context, string? documentHandle, BomStructure structure, string? path = null, bool overwrite = false)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string document = context.DocumentHandle((SolidEdgeDocument)assembly);
        if (structure == BomStructure.Statistics)
        {
            return Statistics(context, assembly, document, path, overwrite);
        }

        bool exploded = structure switch
        {
            BomStructure.Indented => false,
            BomStructure.Exploded => true,
            _ => throw new ArgumentOutOfRangeException(nameof(structure)),
        };
        // A file gets every line; the result, the first ones.
        var lines = new List<ReportLine>();
        int total = Indent(TopNodes(assembly), 1, "", exploded, lines, path is null ? context.MaxListItems : int.MaxValue);
        string? savedTo = path is null ? null : TableFile.Write(context, path, overwrite,
            exploded ? ["Level", "Item", "Name", "Occurrence", "Kind", "Quantity", "File"] : ["Level", "Item", "Name", "Kind", "Quantity", "File"],
            lines.Select(l => (IReadOnlyList<object?>)(exploded
                ? [l.Level, l.Item, l.Name, l.Occurrence, l.Kind, l.Quantity, l.File]
                : [l.Level, l.Item, l.Name, l.Kind, l.Quantity, l.File])));
        List<ReportLine> shown = [.. lines.Take(context.MaxListItems)];
        return new { document, structure = ArgumentNames.Snake(structure), count = total, returned = shown.Count, more = total > shown.Count, lines = shown, savedTo };
    }

    /// <summary>Lines of one level and, under each subassembly, the next; returns how many lines the whole report has.</summary>
    private static int Indent(IEnumerable<BomNode> nodes, int level, string prefix, bool exploded, List<ReportLine> lines, int max)
    {
        int total = 0;
        List<BomNode> shown = [.. nodes.Where(n => n.InBom)];
        List<(BomNode First, int Quantity, string? Occurrence)> items = exploded
            ? [.. shown.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).Select(n => (n, n.Quantity, (string?)n.Name))]
            : [.. shown.GroupBy(n => n.File, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => Path.GetFileName(g.Key), StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.First(), g.Sum(n => n.Quantity), (string?)null))];
        for (int i = 0; i < items.Count; i++)
        {
            (BomNode node, int quantity, string? occurrence) = items[i];
            string item = $"{prefix}{i + 1}";
            total++;
            if (lines.Count < max)
            {
                lines.Add(new ReportLine(level, item, Path.GetFileNameWithoutExtension(node.File), occurrence, node.File,
                    node.Subassembly ? "assembly" : KindOf(node.File), quantity));
            }

            if (node.Subassembly)
            {
                total += Indent(node.Children(), level + 1, item + ".", exploded, lines, max);
            }
        }

        return total;
    }

    private static object Statistics(SeContext context, AssemblyDocument assembly, string document, string? path, bool overwrite)
    {
        var parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var subassemblies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int levels = 0;
        void Walk(IEnumerable<BomNode> nodes, int depth)
        {
            foreach (BomNode node in nodes)
            {
                levels = Math.Max(levels, depth);
                Dictionary<string, int> counts = node.Subassembly ? subassemblies : parts;
                counts[node.File] = counts.GetValueOrDefault(node.File) + 1;
                if (node.Subassembly)
                {
                    Walk(node.Children(), depth + 1);
                }
            }
        }

        Walk(TopNodes(assembly), 1);
        var unique = parts.Select(p => (p.Key, p.Value, Kind: KindOf(p.Key)))
            .Concat(subassemblies.Select(s => (s.Key, s.Value, Kind: "assembly")))
            .OrderBy(u => Path.GetFileName(u.Key), StringComparer.OrdinalIgnoreCase)
            .ToList();
        string? savedTo = path is null ? null : TableFile.Write(context, path, overwrite, ["Name", "Kind", "Occurrences", "File"],
            unique.Select(u => (IReadOnlyList<object?>)[Path.GetFileNameWithoutExtension(u.Key), u.Kind, u.Value, u.Key]));
        return new
        {
            document,
            structure = "statistics",
            parts = new { total = parts.Values.Sum(), unique = parts.Count },
            subassemblies = new { total = subassemblies.Values.Sum(), unique = subassemblies.Count },
            levels,
            more = unique.Count > context.MaxListItems,
            components = unique.Take(context.MaxListItems)
                .Select(u => new { name = Path.GetFileNameWithoutExtension(u.Key), file = u.Key, kind = u.Kind, occurrences = u.Value }).ToList(),
            savedTo,
        };
    }

    private static IEnumerable<BomNode> TopNodes(AssemblyDocument assembly) =>
        Items<Occurrence>(assembly.Occurrences).Select(o => new BomNode(o.Name, o.OccurrenceFileName, o.Subassembly, Math.Max(o.Quantity, 1),
            o.IncludeInBom, () => o.Subassembly ? SubNodes(o.SubOccurrences) : []));

    private static IEnumerable<BomNode> SubNodes(SubOccurrences? children) => children is null ? [] : Items<SubOccurrence>(children).Select(s =>
    {
        // The occurrence in the subassembly's own document carries the quantity and whether it is in the BOM.
        Occurrence? own = Late.TryGet(s, "ThisAsOccurrence") as Occurrence;
        return new BomNode(s.Name, s.SubOccurrenceFileName, s.Subassembly, own is null ? 1 : Math.Max(own.Quantity, 1), own?.IncludeInBom ?? true,
            () => s.Subassembly ? SubNodes(s.SubOccurrences) : []);
    });

    /// <summary>A line of the indented or exploded bill of materials (the occurrence name only in the exploded one).</summary>
    private sealed record ReportLine(int Level, string Item, string Name, string? Occurrence, string File, string Kind, int Quantity);

    /// <summary>An occurrence at any level of the assembly, with its children read on demand.</summary>
    private sealed record BomNode(string Name, string File, bool Subassembly, int Quantity, bool InBom, Func<IEnumerable<BomNode>> Children);
}
