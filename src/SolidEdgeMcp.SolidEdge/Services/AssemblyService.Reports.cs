// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
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
    public static object Report(SeContext context, string? documentHandle, string structure)
    {
        AssemblyDocument assembly = Assembly(context, documentHandle);
        string document = context.DocumentHandle((SolidEdgeDocument)assembly);
        if (structure == "statistics")
        {
            return Statistics(context, assembly, document);
        }

        bool exploded = structure switch
        {
            "indented" => false,
            "exploded" => true,
            _ => throw ToolException.InvalidArgument($"Unknown structure '{structure}'.", "Use top, flat, indented, exploded or statistics."),
        };
        var lines = new List<object>();
        int total = Indent(TopNodes(assembly), 1, "", exploded, lines, context.MaxListItems);
        return new { document, structure, count = total, returned = lines.Count, more = total > lines.Count, lines };
    }

    /// <summary>Lines of one level and, under each subassembly, the next; returns how many lines the whole report has.</summary>
    private static int Indent(IEnumerable<BomNode> nodes, int level, string prefix, bool exploded, List<object> lines, int max)
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
                lines.Add(new
                {
                    level,
                    item,
                    name = Path.GetFileNameWithoutExtension(node.File),
                    occurrence,
                    file = node.File,
                    kind = node.Subassembly ? "assembly" : KindOf(node.File),
                    quantity,
                });
            }

            if (node.Subassembly)
            {
                total += Indent(node.Children(), level + 1, item + ".", exploded, lines, max);
            }
        }

        return total;
    }

    private static object Statistics(SeContext context, AssemblyDocument assembly, string document)
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

    /// <summary>An occurrence at any level of the assembly, with its children read on demand.</summary>
    private sealed record BomNode(string Name, string File, bool Subassembly, int Quantity, bool InBom, Func<IEnumerable<BomNode>> Children);
}
