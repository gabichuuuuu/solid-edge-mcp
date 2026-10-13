// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using SolidEdgeAssembly;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using SolidEdgePart;

namespace SolidEdgeMcp.SolidEdge.Services;

public static partial class LinkService
{
    private static readonly string[] PackKinds = [".asm", ".dft", ".par", ".psm"];

    /// <summary>
    /// Pack and Go: a document and every file it refers to (components of assemblies at every level, the models of a drawing,
    /// the sources of copied parts) copied into one folder, and the copies linked to one another. Verified in Solid Edge 2026:
    /// a copied assembly opened from the folder already finds the components copied next to it; a link that still points
    /// outside is replaced (Occurrence.Replace, ModelLink.ChangeSource, CopiedPart.FileName) and every copy is checked by
    /// reading its links back. The original files are only read.
    /// </summary>
    public static object Pack(SeContext context, string? path, string? folder, bool overwrite)
    {
        string top = context.Paths.Check(path ?? throw ToolException.InvalidArgument("pack needs 'path': the document to pack (.asm, .dft, .par or .psm)."));
        string target = context.Paths.Check(folder ?? throw ToolException.InvalidArgument("pack needs 'folder': where the copies go."), "folder");
        if (!File.Exists(top))
        {
            throw ToolException.NotFound($"The file does not exist: '{top}'.");
        }

        if (!PackKinds.Contains(System.IO.Path.GetExtension(top).ToLowerInvariant()))
        {
            throw ToolException.InvalidArgument("pack takes an assembly, drawing, part or sheet metal file.");
        }

        if (Same(System.IO.Path.GetDirectoryName(top), System.IO.Path.GetFullPath(target).TrimEnd('\\')))
        {
            throw ToolException.InvalidArgument("'folder' must be another folder than the document's.");
        }

        // Every file of the set, read from the documents themselves (opened for the reading and closed again).
        var files = new List<string> { top };
        var pending = new Queue<string>([top]);
        while (pending.Count > 0)
        {
            foreach (string reference in References(context, pending.Dequeue()))
            {
                if (!files.Any(f => Same(f, reference)))
                {
                    files.Add(reference);
                    pending.Enqueue(reference);
                }
            }
        }

        string[] missing = [.. files.Where(f => !File.Exists(f))];
        if (missing.Length > 0)
        {
            throw ToolException.NotFound($"Some referenced files do not exist: {string.Join(", ", missing)}.", "Repair the links first (se_links list, replace).");
        }

        string[] clashes = [.. files.GroupBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)];
        if (clashes.Length > 0)
        {
            throw ToolException.InvalidArgument($"Different files have the same name and cannot share one folder: {string.Join(", ", clashes)}.");
        }

        SolidEdgeDocument? open = files.Select(f => DocumentService.FindOpen(context, f)).FirstOrDefault(d => d is not null);
        if (open is not null)
        {
            throw new ToolException("document_open", $"'{open.Name}' is open in Solid Edge.", "Close the documents of the set before packing them.");
        }

        Directory.CreateDirectory(target);
        Dictionary<string, string> copies = files.ToDictionary(f => f, f => System.IO.Path.Combine(target, System.IO.Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
        string[] existing = [.. copies.Values.Where(File.Exists)];
        if (existing.Length > 0 && !overwrite)
        {
            throw new ToolException("file_exists", $"The folder already has {string.Join(", ", existing.Select(System.IO.Path.GetFileName))}.", "Pass overwrite=true to replace them, or give an empty folder.");
        }

        foreach ((string source, string copy) in copies)
        {
            File.Copy(source, copy, overwrite);
        }

        int relinked = 0;
        foreach (string copy in copies.Values.Where(c => System.IO.Path.GetExtension(c).ToLowerInvariant() is ".asm" or ".dft" or ".par" or ".psm"))
        {
            relinked += Relink(context, copy, target);
        }

        string[] outside = [.. copies.Values.SelectMany(c => References(context, c)).Where(r => !Same(System.IO.Path.GetDirectoryName(r), System.IO.Path.GetFullPath(target).TrimEnd('\\')))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (outside.Length > 0)
        {
            throw new ToolException("pack_failed", $"Some copies still refer to files outside the folder: {string.Join(", ", outside)}.",
                "Open the copies and repair them with se_links replace.");
        }

        return new
        {
            folder = target,
            document = copies[top],
            count = files.Count,
            files = copies.Values.Select(System.IO.Path.GetFileName).ToArray(),
            relinked,
            note = "Every copy refers only to files in the folder (checked by reading the links of each copy back).",
        };
    }

    /// <summary>The files a document refers to directly, read from it (opened and closed again when it was not open).</summary>
    private static List<string> References(SeContext context, string file)
    {
        return WithDocument<List<string>>(context, file, document => document switch
        {
            AssemblyDocument assembly => [.. AssemblyLinks(assembly, false).Select(l => (string)l["file"]!)],
            DraftDocument draft => [.. Links(draft).Select(l => l.FileName).Distinct(StringComparer.OrdinalIgnoreCase)],
            PartDocument or SheetMetalDocument => [.. CopiedParts(document).Select(c => c.FileName).Distinct(StringComparer.OrdinalIgnoreCase)],
            _ => [],
        }, save: false);
    }

    /// <summary>Points the links of a copy that lead outside the folder to the copies of the same name; returns how many.</summary>
    private static int Relink(SeContext context, string copy, string folder) => WithDocument<int>(context, copy, document =>
    {
        string Inside(string file) => System.IO.Path.Combine(folder, System.IO.Path.GetFileName(file));
        bool Outside(string file) => !Same(System.IO.Path.GetDirectoryName(file), System.IO.Path.GetFullPath(folder).TrimEnd('\\'));
        int changed = 0;
        switch (document)
        {
            case AssemblyDocument assembly:
                foreach (string file in AssemblyService.Items<Occurrence>(assembly.Occurrences).Select(o => o.OccurrenceFileName)
                    .Where(Outside).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    AssemblyService.Items<Occurrence>(assembly.Occurrences).First(o => Same(o.OccurrenceFileName, file)).Replace(Inside(file), true);
                    changed++;
                }

                break;
            case DraftDocument draft:
                foreach (ModelLink link in Links(draft).Where(l => Outside(l.FileName)).ToList())
                {
                    link.ChangeSource(Inside(link.FileName));
                    link.UpdateViews();
                    changed++;
                }

                break;
            case PartDocument or SheetMetalDocument:
                foreach (CopiedPart part in CopiedParts(document).Where(c => Outside(c.FileName)).ToList())
                {
                    part.FileName = Inside(part.FileName);
                    changed++;
                }

                break;
        }

        return changed;
    }, save: true);

    /// <summary>
    /// Runs <paramref name="work"/> on a file of the set: a document already open is used as it is and left open; otherwise
    /// the file is opened and closed again (saved first if asked and changed).
    /// </summary>
    private static T WithDocument<T>(SeContext context, string file, Func<SolidEdgeDocument, T> work, bool save)
    {
        SolidEdgeDocument? already = DocumentService.FindOpen(context, file);
        SolidEdgeDocument document = already ?? (SolidEdgeDocument)context.Application.Documents.Open(file);
        try
        {
            T result = work(document);
            if (save && already is null && document.Dirty)
            {
                document.Save();
            }

            return result;
        }
        finally
        {
            if (already is null)
            {
                Late.Call(document, "Close", false);
            }
        }
    }
}
