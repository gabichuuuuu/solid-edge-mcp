// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using SolidEdgeAssembly;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using Path = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>Missing component files of an assembly (Repair Missing Files) and CAD Direct links to files of other CAD programs.</summary>
public static partial class LinkService
{
    /// <summary>Extensions of Solid Edge documents, which are placed with se_assembly insert rather than through CAD Direct.</summary>
    private static readonly string[] SolidEdgeExtensions = [".par", ".psm", ".asm", ".dft", ".pwd"];

    /// <summary>
    /// The component files the assembly cannot find and, with <paramref name="recheck"/>, a new look for them after they were
    /// put back. Verified in Solid Edge 2026: with a component file moved away before opening, HasMissingFiles is true and
    /// GetMissingFilesInfo gives the file with its occurrences; once the file is back, Occurrence.RecheckMissingFile() returns
    /// true and HasMissingFiles turns false.
    /// </summary>
    public static object Missing(SeContext context, string? documentHandle, bool recheck)
    {
        AssemblyDocument assembly = AssemblyService.Assembly(context, documentHandle);
        string[] before = MissingFiles(assembly);
        var found = new List<string>();
        if (recheck)
        {
            foreach (Occurrence occurrence in AssemblyService.Items<Occurrence>(assembly.Occurrences)
                .Where(o => before.Any(f => Same(f, o.OccurrenceFileName))))
            {
                if (occurrence.RecheckMissingFile())
                {
                    found.Add(occurrence.OccurrenceFileName);
                }
            }

            if (found.Count > 0)
            {
                context.ModelChanged((SolidEdgeDocument)assembly, "missing component files were found");
            }
        }

        string[] missing = MissingFiles(assembly);
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            hasMissing = missing.Length > 0,
            missing,
            found = recheck ? found.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : null,
            note = missing.Length > 0 ? "Put the files back and recheck, or point the components at other files (action 'replace')." : null,
        };
    }

    /// <summary>
    /// CAD Direct: a file of another CAD program placed in the assembly as a component that stays linked to it. Verified in
    /// Solid Edge 2026: AsmCADDirects.Add(step file) of a saved assembly adds an occurrence of a part translated from it
    /// (product_stp.par, an internal component kept inside the assembly file) with the geometry of the file;
    /// AsmCADDirect.UpdateFeature reads the file again (Update does not).
    /// </summary>
    public static object CadDirect(SeContext context, string? documentHandle, string? path)
    {
        AssemblyDocument assembly = AssemblyService.Assembly(context, documentHandle);
        RequireSaved(assembly);
        string file = context.Paths.Check(path ?? throw ToolException.InvalidArgument("'path' is required: the file of the other CAD program (STEP, IGES, Parasolid, JT…)."), "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The file '{file}' does not exist.");
        }

        if (SolidEdgeExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(file)}' is a Solid Edge document.", "Place it with se_assembly insert.");
        }

        Occurrence[] before = [.. AssemblyService.Items<Occurrence>(assembly.Occurrences)];
        int links = assembly.AsmCADDirects.Count;
        AsmCADDirect link;
        try
        {
            link = assembly.AsmCADDirects.Add(file);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            throw new ToolException("insert_failed", $"Solid Edge did not place '{Path.GetFileName(file)}': {ex.Message.Trim()}", "Check that Solid Edge opens this format (se_documents open).", ex);
        }

        Occurrence[] added = [.. AssemblyService.Items<Occurrence>(assembly.Occurrences).Where(o => !before.Any(b => Late.SameObject(b, o)))];
        if (assembly.AsmCADDirects.Count != links + 1 || added.Length == 0)
        {
            throw new ToolException("insert_failed", $"Solid Edge did not place a component from '{Path.GetFileName(file)}'.", "Check that Solid Edge opens this format (se_documents open).");
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "a CAD Direct component was added");
        HashSet<int> grounded = AssemblyService.GroundedIds(assembly);
        Dictionary<string, object?>[] rows = [.. added.Select(o => DescribeCad(context, assembly, o, grounded))];
        return new
        {
            source = link.SourcePath(),
            occurrences = rows,
            links = CadLinks(assembly),
            note = NoGeometryNote(rows),
        };
    }

    /// <summary>
    /// Verified with Solid Edge 2026 Foundation: AsmCADDirects.Add of a STEP file places the occurrence with no geometry (empty
    /// range, nothing on screen, also after saving and reopening), while se_documents import translates the same file.
    /// </summary>
    private static string? NoGeometryNote(IEnumerable<Dictionary<string, object?>> rows) =>
        rows.Any(r => r["geometry"] is false)
            ? "Solid Edge placed the CAD Direct component without geometry (as Solid Edge 2026 Foundation does): se_documents import opens the file as a part, which se_assembly insert can place."
            : null;

    /// <summary>Reads the CAD Direct files again (all, or the one whose source is <paramref name="from"/>).</summary>
    public static object CadUpdate(SeContext context, string? documentHandle, string? from)
    {
        AssemblyDocument assembly = AssemblyService.Assembly(context, documentHandle);
        AsmCADDirect[] links = [.. AssemblyService.Items<AsmCADDirect>(assembly.AsmCADDirects)];
        if (links.Length == 0)
        {
            throw new ToolException("no_links", "The assembly has no CAD Direct components.", "Add one with action 'cad_direct'.");
        }

        AsmCADDirect[] chosen = from is null ? links : [.. links.Where(l => Matches(l.SourcePath(), from.Trim()))];
        if (chosen.Length == 0)
        {
            throw ToolException.NotFound($"No CAD Direct component comes from '{from}'.", $"Sources: {string.Join(", ", links.Select(l => l.SourcePath()))}.");
        }

        // Verified: Update leaves the translated part as it was (also after saving and reopening); UpdateFeature reads the
        // source file again and the part takes its new geometry.
        foreach (AsmCADDirect link in chosen)
        {
            link.UpdateFeature();
        }

        context.ModelChanged((SolidEdgeDocument)assembly, "CAD Direct components were updated");
        HashSet<int> grounded = AssemblyService.GroundedIds(assembly);
        Dictionary<string, object?>[] rows = [.. CadOccurrences(assembly, chosen).Select(o => DescribeCad(context, assembly, o, grounded))];
        return new
        {
            document = context.DocumentHandle((SolidEdgeDocument)assembly),
            updated = chosen.Select(l => l.SourcePath()).ToArray(),
            links = CadLinks(assembly),
            occurrences = rows,
            note = NoGeometryNote(rows),
        };
    }

    /// <summary>
    /// The occurrences of the parts translated from <paramref name="links"/>. AsmCADDirect does not name its occurrence; the
    /// part is kept inside the assembly file as an internal component named after the product in the source file plus the
    /// source extension ("assembly.asm\product_stp.par", verified with STEP files), so it is found by that suffix.
    /// </summary>
    private static IEnumerable<Occurrence> CadOccurrences(AssemblyDocument assembly, IEnumerable<AsmCADDirect> links)
    {
        string inside = ((SolidEdgeDocument)assembly).FullName + Path.DirectorySeparatorChar;
        string[] suffixes = [.. links.Select(l => "_" + Path.GetExtension(l.SourcePath()).TrimStart('.') + ".par").Distinct(StringComparer.OrdinalIgnoreCase)];
        return AssemblyService.Items<Occurrence>(assembly.Occurrences).Where(o =>
            o.OccurrenceFileName.StartsWith(inside, StringComparison.OrdinalIgnoreCase)
            && suffixes.Any(s => o.OccurrenceFileName.EndsWith(s, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// An occurrence with its bounding box in assembly coordinates. The part of a CAD Direct component cannot be opened
    /// (E_ACCESSDENIED), its Body gives E_FAIL and the assembly's physical properties leave it out (verified in Solid Edge
    /// 2026), so the box from Occurrence.Range is what shows its geometry. Without geometry the range is inverted (±1e33):
    /// the row then has no box and geometry false.
    /// </summary>
    private static Dictionary<string, object?> DescribeCad(SeContext context, AssemblyDocument assembly, Occurrence occurrence, HashSet<int> grounded)
    {
        Dictionary<string, object?> row = AssemblyService.Describe(context, assembly, occurrence, grounded);
        occurrence.Range(out double x0, out double y0, out double z0, out double x1, out double y1, out double z1);
        row["geometry"] = x1 >= x0;
        row["box"] = x1 < x0 ? null : new
        {
            min = new[] { Geo.Mm(x0), Geo.Mm(y0), Geo.Mm(z0) },
            max = new[] { Geo.Mm(x1), Geo.Mm(y1), Geo.Mm(z1) },
            size = new[] { Geo.Mm(x1 - x0), Geo.Mm(y1 - y0), Geo.Mm(z1 - z0) },
        };
        return row;
    }

    private static object[] CadLinks(AssemblyDocument assembly) =>
        [.. AssemblyService.Items<AsmCADDirect>(assembly.AsmCADDirects).Select(l => new { name = l.Name, source = l.SourcePath(), exists = File.Exists(l.SourcePath()) })];

    private static void RequireSaved(AssemblyDocument assembly)
    {
        var document = (SolidEdgeDocument)assembly;
        if (!Path.IsPathRooted(document.FullName))
        {
            throw new ToolException("unsaved_changes", "Save the assembly first: the translated component is written next to it.", "se_documents save_as.");
        }
    }

    private static string[] MissingFiles(AssemblyDocument assembly)
    {
        if (!assembly.HasMissingFiles)
        {
            return [];
        }

        // Verified: a two-dimensional array that holds each missing file name and the array of its occurrences.
        assembly.GetMissingFilesInfo(out object info);
        return info is Array rows ? [.. rows.Cast<object?>().OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)] : [];
    }
}
