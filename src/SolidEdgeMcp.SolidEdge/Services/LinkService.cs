// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeAssembly;
using SolidEdgeDraft;
using SolidEdgeFramework;
using SolidEdgePart;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Links between files, as the program manages them: what a document refers to (components of an assembly, models of a
/// drawing, copied parts of a part), replacing a referenced file, where a file is used, the top-level assemblies of a set
/// and the document search of File > Open.
/// </summary>
public static partial class LinkService
{
    /// <summary>Most files a search or a where-used scan walks.</summary>
    private const int MaxFiles = 5000;

    public static object List(SeContext context, string? documentHandle, bool recursive)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        List<Dictionary<string, object?>> links = document switch
        {
            AssemblyDocument assembly => AssemblyLinks(assembly, recursive),
            DraftDocument draft => DrawingLinks(context, draft),
            PartDocument or SheetMetalDocument => CopiedParts(document).Select(c => Describe(context, document, c)).ToList(),
            _ => throw new ToolException("not_supported", $"'{document.Name}' is not a part, sheet metal, assembly or drawing."),
        };
        return new
        {
            document = context.DocumentHandle(document),
            kind = DocumentKinds.ToKind(document.Type),
            count = links.Count,
            missing = links.Count(l => l["exists"] is false),
            links,
        };
    }

    /// <summary>
    /// Replaces a referenced file: the components of an assembly (Occurrence.Replace, every occurrence of that file), the
    /// model of a drawing (ModelLink.ChangeSource, then its views are updated) or the source of copied parts.
    /// </summary>
    public static object Replace(SeContext context, string? documentHandle, string? from, string? to)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        string old = string.IsNullOrWhiteSpace(from)
            ? throw ToolException.InvalidArgument("'from' is required: the referenced file to replace (full path or file name, see action 'list').")
            : from.Trim();
        string target = context.Paths.Check(to, "to");
        if (!File.Exists(target))
        {
            throw ToolException.NotFound($"The file '{target}' does not exist.");
        }

        switch (document)
        {
            case AssemblyDocument assembly:
            {
                List<Occurrence> matches = AssemblyService.Items<Occurrence>(assembly.Occurrences).Where(o => Matches(o.OccurrenceFileName, old)).ToList();
                string oldFile = Single(matches.Select(o => o.OccurrenceFileName), old, AssemblyLinks(assembly, false));
                RequireKind(oldFile, target, ".par", ".psm", ".asm");
                matches[0].Replace(target, true);
                context.ModelChanged(document, "a component was replaced");
                List<Occurrence> after = AssemblyService.Items<Occurrence>(assembly.Occurrences).ToList();
                int left = after.Count(o => Same(o.OccurrenceFileName, oldFile));
                int now = after.Count(o => Same(o.OccurrenceFileName, target));
                if (left > 0 || now < matches.Count)
                {
                    throw new ToolException("replace_failed", $"{left} occurrence(s) still use '{oldFile}'.", "Check the assembly with se_links 'list'.");
                }

                return new { document = context.DocumentHandle(document), from = oldFile, to = target, replaced = matches.Count };
            }

            case DraftDocument draft:
            {
                List<ModelLink> matches = Links(draft).Where(l => Matches(l.FileName, old)).ToList();
                string oldFile = Single(matches.Select(l => l.FileName), old, DrawingLinks(context, draft));
                RequireKind(oldFile, target, ".par", ".psm", ".asm", ".pwd");
                foreach (ModelLink link in matches)
                {
                    link.ChangeSource(target);
                    link.UpdateViews();
                }

                context.ModelChanged(document, "a drawing model was replaced");
                List<Dictionary<string, object?>> links = DrawingLinks(context, draft);
                if (matches.Any(l => !Same(l.FileName, target) || l.ModelOutOfDate))
                {
                    throw new ToolException("replace_failed", $"The drawing still refers to '{oldFile}' or its views are out of date.", "Check it with se_links 'list'.");
                }

                return new { document = context.DocumentHandle(document), from = oldFile, to = target, replaced = matches.Count, links };
            }

            case PartDocument or SheetMetalDocument:
            {
                ModelDocument model = ModelDocument.From(document);
                List<CopiedPart> matches = CopiedParts(document).Where(c => Matches(c.FileName, old)).ToList();
                string oldFile = Single(matches.Select(c => c.FileName), old, CopiedParts(document).Select(c => Describe(context, document, c)).ToList());
                RequireKind(oldFile, target, ".par", ".psm");
                double before = model.Volume();
                foreach (CopiedPart copy in matches)
                {
                    copy.FileName = target;
                    copy.Update();
                }

                context.ModelChanged(document, "a copied part was replaced");
                if (matches.Any(c => !Same(c.FileName, target)))
                {
                    throw new ToolException("replace_failed", $"A copied part still refers to '{oldFile}'.", "Check it with se_links 'list'.");
                }

                return new
                {
                    document = context.DocumentHandle(document),
                    from = oldFile,
                    to = target,
                    replaced = matches.Count,
                    volume = new { before = Geo.Mm3(before), after = Geo.Mm3(model.Volume()) },
                    links = matches.Select(c => Describe(context, document, c)).ToList(),
                };
            }

            default:
                throw new ToolException("not_supported", $"'{document.Name}' is not a part, sheet metal, assembly or drawing.");
        }
    }

    /// <summary>Breaks the link of copied parts to their source file: the geometry stays and no longer updates (it cannot be undone).</summary>
    public static object Break(SeContext context, string? documentHandle, string? from)
    {
        SolidEdgeDocument document = context.GetDocument(documentHandle);
        if (document is not (PartDocument or SheetMetalDocument))
        {
            throw new ToolException("not_supported", "Only the copied parts of a part or sheet metal document can be broken.",
                "In an assembly, replace or delete the occurrence; in a drawing, convert the views to 2D (se_drawing_view to2d).");
        }

        string old = string.IsNullOrWhiteSpace(from)
            ? throw ToolException.InvalidArgument("'from' is required: the source file of the copied parts (full path or file name, see action 'list').")
            : from.Trim();
        ModelDocument model = ModelDocument.From(document);
        List<CopiedPart> matches = CopiedParts(document).Where(c => !c.IsBroken && Matches(c.FileName, old)).ToList();
        string oldFile = Single(matches.Select(c => c.FileName), old, CopiedParts(document).Select(c => Describe(context, document, c)).ToList());
        double before = model.Volume();
        foreach (CopiedPart copy in matches)
        {
            copy.BreakLinks();
        }

        context.ModelChanged(document, "copied parts were unlinked");
        double after = model.Volume();
        if (matches.Any(c => !c.IsBroken) || Math.Abs(after - before) > 1e-9 * Math.Max(1, before) + 1e-12)
        {
            throw new ToolException("break_failed", $"The link to '{oldFile}' was not broken cleanly (volume {Geo.Mm3(before)} → {Geo.Mm3(after)} mm³).",
                "Check the part with se_links 'list' and se_physical.");
        }

        return new { document = context.DocumentHandle(document), from = oldFile, broken = matches.Count, volume = Geo.Mm3(after) };
    }

    /// <summary>
    /// Documents that use a file (FindWhereUsedDocuments): the assemblies, drawings and parts in the given folders that refer
    /// to it directly; with <paramref name="indirect"/>, also those that use them, up to the top. When Solid Edge's search finds
    /// none, the links of the documents of the folders are read with Design Manager instead (<see cref="DesignManagerService.LinksIn"/>).
    /// </summary>
    public static object WhereUsed(SeContext context, string? path, string[]? folders, bool subfolders, bool indirect)
    {
        string file = context.Paths.Check(path, "path");
        List<string> scope = Folders(context, folders, System.IO.Path.GetDirectoryName(file)!);
        List<string> direct = [.. UsersOf(context, file, scope, subfolders)];
        DesignManagerService.LinkScan? scanned = direct.Count == 0 ? ScanLinks(context, scope, subfolders) : null;
        IEnumerable<string> UsersOfFile(string current) => scanned is not null
            ? scanned.Links.Where(d => d.Value.Any(l => Same(l, current))).Select(d => d.Key)
            : Same(current, file) ? direct : UsersOf(context, current, scope, subfolders);
        var users = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file };
        var pending = new Queue<(string File, int Level)>();
        pending.Enqueue((file, 0));
        while (pending.Count > 0 && users.Count < MaxFiles)
        {
            (string current, int level) = pending.Dequeue();
            foreach (string user in UsersOfFile(current))
            {
                if (!seen.Add(user))
                {
                    continue;
                }

                users.Add(new { file = user, kind = KindOf(user), level = level + 1, uses = current });
                if (indirect && KindOf(user) is "assembly" or "part" or "sheet_metal")
                {
                    pending.Enqueue((user, level + 1));
                }
            }
        }

        return new
        {
            file,
            folders = scope,
            subfolders,
            indirect,
            searchedBy = scanned is null ? "solid_edge" : "design_manager",
            count = users.Count,
            users,
            skipped = scanned is { Skipped.Count: > 0 } ? scanned.Skipped : null,
            truncated = scanned is { Truncated: true } ? true : (bool?)null,
            note = scanned is not null
                ? "Design Manager read the saved files, so what is not saved in an open document does not count."
                    + (scanned.Skipped.Count > 0 ? " It could not read the files in 'skipped', which may use this one." : "")
                    + (scanned.Truncated ? " The folders hold more documents than it reads in one call, so some were left out." : "")
                : users.Count == 0
                    ? "Solid Edge's search found no users and Design Manager, which would read the links of the documents instead, is not available."
                    : null,
        };
    }

    /// <summary>
    /// The links of the documents of the folders, read with Design Manager; null when it is not installed. Verified with Solid
    /// Edge 2026 Foundation: its search (FindWhereUsedDocuments) found no users of a part used by an assembly and a drawing of
    /// the same folder (A25), with Fast Search on, which reads the Windows Search index, and the search not limited to a
    /// scope; that the index not covering the folder is the cause was not checked (it needs Solid Edge restarted).
    /// </summary>
    private static DesignManagerService.LinkScan? ScanLinks(SeContext context, List<string> scope, bool subfolders)
    {
        try
        {
            return DesignManagerService.LinksIn(context, scope, subfolders);
        }
        catch (ToolException ex) when (ex.Code == "not_available")
        {
            return null;
        }
    }

    /// <summary>Assemblies of a set that no other assembly of the set uses (GetTopLevelAssemblyFileNames).</summary>
    public static object TopLevel(SeContext context, string[]? files, string? folder, bool subfolders)
    {
        List<string> assemblies;
        if (files is { Length: > 0 })
        {
            assemblies = files.Select(f => context.Paths.Check(f, "files")).ToList();
        }
        else if (!string.IsNullOrWhiteSpace(folder))
        {
            string root = ExistingFolder(context, folder, "folder");
            assemblies = Directory.EnumerateFiles(root, "*.asm", subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Take(MaxFiles).ToList();
        }
        else
        {
            throw ToolException.InvalidArgument("Pass 'files' (assembly paths) or 'folder' (its .asm files).");
        }

        if (assemblies.FirstOrDefault(f => !File.Exists(f)) is { } missing)
        {
            throw ToolException.NotFound($"The file '{missing}' does not exist.");
        }

        if (assemblies.Count == 0)
        {
            return new { @checked = 0, count = 0, topLevel = Array.Empty<string>() };
        }

        // Typed, the output array gives E_POINTER (verified): it must already exist, which only IDispatch passes; there both
        // arrays must be of strings (arrays of variants, or no output array, give DISP_E_TYPEMISMATCH).
        object?[] args = Late.CallByRef(context.Application, "GetTopLevelAssemblyFileNames", [assemblies.ToArray(), Array.Empty<string>()], 0, 1);
        List<string> top = Strings(args[1]);
        return new { @checked = assemblies.Count, count = top.Count, topLevel = top };
    }

    /// <summary>
    /// Solid Edge documents of a folder (SearchDocuments, the search of File > Open) with their title, subject and date of
    /// change; <paramref name="text"/> keeps those whose title, subject or file name contains it, or with
    /// <paramref name="contents"/> those whose contents contain it (FindSEDocumentsContainingText, through the Windows Search
    /// index). Verified: a drawing with a text box "Zorblaxite note" in an indexed folder is found; the output array must be an
    /// empty array of strings passed by reference through IDispatch (typed, E_POINTER; an array of variants, an exception) and the scope an array of variants.
    /// </summary>
    public static object Search(SeContext context, string? folder, string? filter, bool subfolders, string? text, int offset, int limit,
        bool contents = false)
    {
        string root = ExistingFolder(context, folder, "folder");
        HashSet<string>? containing = null;
        if (contents)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw ToolException.InvalidArgument("contents=true searches the text inside the documents: give 'text'.");
            }

            object?[] found0 = Late.CallByRef(context.Application, "FindSEDocumentsContainingText",
                [text, new object[] { root }, "", true, Array.Empty<string>()], 1, 4);
            // Verified: without subfolders it finds nothing, so subfolders are searched and left out here when not asked for.
            containing = Strings(found0[4]).Where(f => subfolders || string.Equals(System.IO.Path.GetDirectoryName(f), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        // Verified: several patterns in one call ('*.par;*.dft') can find nothing in a folder where each pattern alone finds its
        // file, and a second call right after the first gives DISP_E_BADCALLEE; so one call lists every file and the patterns
        // are applied here (without a pattern SearchDocuments also lists other files, such as .cfg).
        string[] patternList = (string.IsNullOrWhiteSpace(filter) ? "*.par;*.psm;*.asm;*.dft;*.pwd" : filter)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string pattern = string.Join(";", patternList);
        object none = System.Reflection.Missing.Value;
        context.Application.SearchDocuments(false, root, subfolders, out object files, out int _, "", none, none, none, none, none, none,
            out object titles, out object subjects, out object dates);
        List<string> titleList = Strings(titles, keepEmpty: true);
        List<string> subjectList = Strings(subjects, keepEmpty: true);
        List<string> dateList = Strings(dates, keepEmpty: true);
        List<(string File, string? Title, string? Subject, string? Modified)> found =
            [.. Strings(files).Select((f, i) => (f, titleList.ElementAtOrDefault(i), subjectList.ElementAtOrDefault(i), dateList.ElementAtOrDefault(i)))
                .Where(r => patternList.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, System.IO.Path.GetFileName(r.f))))];

        if (containing is not null)
        {
            // The content search does not depend on SearchDocuments, which can list nothing for a folder it does not index.
            found.AddRange(containing.Where(f => !found.Any(r => string.Equals(r.File, f, StringComparison.OrdinalIgnoreCase))
                && patternList.Any(p => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, System.IO.Path.GetFileName(f))))
                .Select(f => (f, (string?)null, (string?)null, (string?)null)));
        }

        var rows = found.DistinctBy(f => f.File, StringComparer.OrdinalIgnoreCase).Take(MaxFiles).Select(f => new
        {
            file = f.File,
            kind = KindOf(f.File),
            title = f.Title,
            subject = f.Subject,
            modified = f.Modified,
        }).Where(r => containing is not null ? containing.Contains(r.file)
            : string.IsNullOrWhiteSpace(text)
            || System.IO.Path.GetFileName(r.file).Contains(text, StringComparison.OrdinalIgnoreCase)
            || r.title?.Contains(text, StringComparison.OrdinalIgnoreCase) == true
            || r.subject?.Contains(text, StringComparison.OrdinalIgnoreCase) == true).ToList();
        int start = Math.Max(offset, 0);
        var page = rows.Skip(start).Take(Math.Clamp(limit, 1, context.MaxListItems)).ToList();
        string? note = containing is { Count: 0 }
            ? "The contents are searched through the Windows Search index: a folder outside the index, or files saved moments ago, give no results."
            : null;
        return new { folder = root, filter = pattern, subfolders, found = found.Count, count = rows.Count, offset = start, returned = page.Count, documents = page, note };
    }

    private static List<Dictionary<string, object?>> AssemblyLinks(AssemblyDocument assembly, bool recursive)
    {
        var rows = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        // Internal components live inside the assembly file: their file reads "assembly.asm\name.par".
        string inside = ((SolidEdgeDocument)assembly).FullName + System.IO.Path.DirectorySeparatorChar;
        void Add(string file, string key)
        {
            if (!rows.TryGetValue(file, out Dictionary<string, object?>? row))
            {
                bool isInternal = file.StartsWith(inside, StringComparison.OrdinalIgnoreCase);
                row = new Dictionary<string, object?>
                {
                    ["file"] = file,
                    ["kind"] = KindOf(file),
                    ["exists"] = isInternal || File.Exists(file),
                    ["quantity"] = 0,
                };
                if (isInternal)
                {
                    row["internal"] = true;
                }
                rows.Add(file, row);
            }

            row[key] = (int)(row.GetValueOrDefault(key) ?? 0) + 1;
        }

        void AddNested(SubOccurrences? children)
        {
            // An empty subassembly has no SubOccurrences collection (null).
            foreach (SubOccurrence child in children is null ? [] : AssemblyService.Items<SubOccurrence>(children))
            {
                Add(child.SubOccurrenceFileName, "nested");
                if (child.Subassembly)
                {
                    AddNested(child.SubOccurrences);
                }
            }
        }

        foreach (Occurrence occurrence in AssemblyService.Items<Occurrence>(assembly.Occurrences))
        {
            Add(occurrence.OccurrenceFileName, "quantity");
            if (recursive && occurrence.Subassembly)
            {
                AddNested(occurrence.SubOccurrences);
            }
        }

        foreach (SuppressComponent component in AssemblyService.SuppressedComponents(assembly))
        {
            Add(component.OccurrenceFileName, "suppressed");
        }

        return [.. rows.Values];
    }

    private static List<Dictionary<string, object?>> DrawingLinks(SeContext context, DraftDocument draft)
    {
        var viewsByFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (Sheet sheet in Late.Items(draft.Sheets).OfType<Sheet>())
        {
            foreach (DrawingView view in Late.Items(sheet.DrawingViews).OfType<DrawingView>())
            {
                if (Late.TryGet(view, "ModelLink") is { } link && Late.TryGetString(link, "FileName") is { } file)
                {
                    viewsByFile[file] = viewsByFile.GetValueOrDefault(file) + 1;
                }
            }
        }

        return Links(draft).Select(l => new Dictionary<string, object?>
        {
            ["handle"] = context.Register(l, "modellink", (SolidEdgeDocument)draft),
            ["file"] = l.FileName,
            ["kind"] = KindOf(l.FileName),
            ["exists"] = File.Exists(l.FileName),
            ["upToDate"] = !l.ModelOutOfDate,
            ["views"] = viewsByFile.GetValueOrDefault(l.FileName),
        }).ToList();
    }

    private static Dictionary<string, object?> Describe(SeContext context, SolidEdgeDocument document, CopiedPart copy) => new()
    {
        ["handle"] = context.Register(copy, "feature", document),
        ["name"] = copy.Name,
        ["file"] = NullIfEmpty(copy.FileName),
        ["kind"] = KindOf(copy.FileName),
        ["exists"] = File.Exists(copy.FileName),
        ["upToDate"] = TryGet(() => copy.IsUpToDate),
        ["broken"] = TryGet(() => copy.IsBroken),
        ["linkMaterial"] = TryGet(() => copy.LinkMaterial),
    };

    private static IEnumerable<ModelLink> Links(DraftDocument draft) => Late.Items(draft.ModelLinks).OfType<ModelLink>();

    private static List<CopiedPart> CopiedParts(SolidEdgeDocument document) =>
        Late.Items(ModelDocument.From(document).Models).OfType<Model>()
            .SelectMany(m => Late.Items(m.CopiedParts).OfType<CopiedPart>()).ToList();

    private static IEnumerable<string> UsersOf(SeContext context, string file, List<string> scope, bool subfolders)
    {
        // String arrays give DISP_E_TYPEMISMATCH (verified): Solid Edge expects arrays of variants.
        Array folders = scope.Cast<object>().ToArray();
        Array filters = Array.Empty<object>();
        object? result = context.Application.FindWhereUsedDocuments(file, ref folders, subfolders, ref filters);
        return Strings(result);
    }

    private static List<string> Folders(SeContext context, string[]? folders, string fallback) =>
        folders is { Length: > 0 }
            ? folders.Select(f => ExistingFolder(context, f, "folders")).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [ExistingFolder(context, fallback, "folders")];

    private static string ExistingFolder(SeContext context, string? folder, string argumentName)
    {
        string root = context.Paths.Check(folder, argumentName);
        return Directory.Exists(root) ? root : throw ToolException.NotFound($"The folder '{root}' does not exist.");
    }

    /// <summary>The single referenced file <paramref name="matches"/> point to; otherwise an error that lists the references.</summary>
    private static string Single(IEnumerable<string> matches, string from, IEnumerable<Dictionary<string, object?>> links)
    {
        List<string> files = matches.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string known = string.Join(", ", links.Select(l => l["file"] as string).Where(f => f is not null).Select(f => System.IO.Path.GetFileName(f!)).Distinct());
        return files.Count switch
        {
            1 => files[0],
            0 => throw ToolException.NotFound($"The document does not refer to '{from}'.", $"It refers to: {(known.Length > 0 ? known : "nothing")}."),
            _ => throw ToolException.InvalidArgument($"'{from}' matches {files.Count} referenced files: {string.Join(", ", files)}.", "Pass the full path in 'from'."),
        };
    }

    private static void RequireKind(string from, string to, params string[] extensions)
    {
        string extension = System.IO.Path.GetExtension(to).ToLowerInvariant();
        if (!extensions.Contains(extension))
        {
            throw ToolException.InvalidArgument($"'{System.IO.Path.GetFileName(to)}' cannot replace '{System.IO.Path.GetFileName(from)}': use a {string.Join(", ", extensions)} file.");
        }
    }

    /// <summary>A reference matches a full path, or a file name when <paramref name="from"/> has no folder.</summary>
    private static bool Matches(string? reference, string from) =>
        reference is not null && (Same(reference, from)
            || (System.IO.Path.GetFileName(from) == from && string.Equals(System.IO.Path.GetFileName(reference), from, StringComparison.OrdinalIgnoreCase)));

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Strings of a COM output (an array, one string or nothing); dates in ISO 8601.</summary>
    private static List<string> Strings(object? value, bool keepEmpty = false) => value switch
    {
        Array array => array.Cast<object?>()
            .Select(v => v is DateTime d ? d.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "")
            .Where(v => keepEmpty || v.Length > 0).ToList(),
        string s when s.Length > 0 => [s],
        _ => [],
    };

    private static T? TryGet<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            return null;
        }
    }

    private static string KindOf(string? file) => System.IO.Path.GetExtension(file)?.ToLowerInvariant() switch
    {
        ".par" => "part",
        ".psm" => "sheet_metal",
        ".asm" => "assembly",
        ".dft" => "draft",
        ".pwd" => "weldment",
        _ => "other",
    };
}
