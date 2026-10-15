// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Export to neutral formats with the Solid Edge translators. The format comes from the extension;
/// the document does not change name or path (it uses "save a copy"). Always through <see cref="PathGuard"/>.
/// </summary>
public static class ExportService
{
    private static readonly Dictionary<string, string> ModelFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".stp"] = "STEP",
        [".step"] = "STEP",
        [".igs"] = "IGES",
        [".iges"] = "IGES",
        [".x_t"] = "Parasolid text",
        [".x_b"] = "Parasolid binary",
        [".stl"] = "STL",
        [".jt"] = "JT",
        [".sat"] = "ACIS",
        [".3mf"] = "3MF",
        [".pdf"] = "PDF",
        [".catpart"] = "CATIA V5",
        [".model"] = "CATIA V4",
        [".obj"] = "OBJ",
        [".plmxml"] = "PLM XML",
        [".u3d"] = "U3D",
        [".xgl"] = "XGL",
    };

    /// <summary>
    /// Formats Solid Edge 2026 writes for parts and sheet metal only. Verified: an assembly saved as .catproduct writes
    /// nothing, as .catpart gives REGDB_E_CLASSNOTREG and as .model gives E_FAIL; OBJ, PLM XML, U3D and XGL take assemblies
    /// too. Any other extension Solid Edge does not translate (.wrl, .bmp, .jpg, .emf, .dgn…) gets a copy in its own format.
    /// </summary>
    private static readonly HashSet<string> PartOnlyFormats = new(StringComparer.OrdinalIgnoreCase) { ".catpart", ".model" };

    /// <summary>The Solid Edge files a batch takes for each kind of format.</summary>
    private static readonly string[] ModelFiles = [".par", ".psm", ".asm", ".pwd"];
    private static readonly string[] DraftFiles = [".dft"];
    private const int MaxBatch = 500;

    private static readonly Dictionary<string, string> DraftFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "PDF",
        [".dxf"] = "DXF",
        [".dwg"] = "DWG",
        [".igs"] = "IGES",
        [".tif"] = "TIFF",
    };

    private static readonly Dictionary<string, string> FlatFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".dxf"] = "DXF",
        [".dwg"] = "DWG",
    };

    public static IEnumerable<string> Extensions(bool draft, bool flatPattern) =>
        (flatPattern ? FlatFormats : draft ? DraftFormats : ModelFormats).Keys;

    public static object Export(SeContext context, string? handle, string path, bool overwrite, bool flatPattern, string[]? bodies = null, string? table = null)
    {
        // The cells of a drawing table go to a spreadsheet file, written by the server (Solid Edge has no call for it).
        if (Path.GetExtension(path).ToLowerInvariant() is ".csv" or ".xlsx")
        {
            if (string.IsNullOrWhiteSpace(table))
            {
                throw ToolException.InvalidArgument("A .csv or .xlsx file takes the cells of a table of a drawing: give 'table' (a handle from se_drawing tables or se_sheet_content list).",
                    "For the bill of materials of an assembly, use se_bom with 'path'.");
            }

            (List<string?> headers, List<string?[]> rows) = DrawingService.TableCells(context, table);
            string saved = TableFile.Write(context, path, overwrite, headers, rows.Select(r => (IReadOnlyList<object?>)[.. r.Cast<object?>()]));
            return new { table, savedTo = saved, columns = headers, count = rows.Count };
        }

        if (!string.IsNullOrWhiteSpace(table))
        {
            throw ToolException.InvalidArgument("'table' is written to a .csv or .xlsx file.", "Give a 'path' ending in .csv or .xlsx.");
        }

        SolidEdgeDocument document = context.GetDocument(handle);
        string kind = DocumentKinds.ToKind(document.Type);
        bool draft = kind == "draft";
        string target = context.Paths.Check(path);
        string extension = Path.GetExtension(target);
        Dictionary<string, string> formats = flatPattern ? FlatFormats : draft ? DraftFormats : ModelFormats;
        if (!formats.TryGetValue(extension, out string? format))
        {
            string what = flatPattern ? "a flat pattern" : $"a {kind.Replace('_', ' ')} document";
            throw ToolException.InvalidArgument($"Cannot export {what} to '{extension}'.",
                $"Use one of: {string.Join(", ", formats.Keys)}." + (draft ? "" : " For drawings, export the draft document."));
        }

        if (!flatPattern && PartOnlyFormats.Contains(extension) && kind is not ("part" or "sheet_metal"))
        {
            throw ToolException.InvalidArgument($"Solid Edge writes {format} files of parts and sheet metal, not of a {kind.Replace('_', ' ')} document.",
                "Export the parts one by one, or the assembly to STEP, JT, Parasolid or PLM XML.");
        }

        object[]? models = null;
        if (bodies is { Length: > 0 })
        {
            models = flatPattern
                ? throw ToolException.InvalidArgument("'bodies' writes chosen bodies of a part, not a flat pattern.")
                : ChosenModels(context, document, bodies);
            _ = SimplifyService.ParasolidType(target);
        }

        if (flatPattern && kind is not ("sheet_metal" or "part"))
        {
            throw new ToolException("wrong_document_type", "Flat patterns exist only in sheet metal (or part) documents.");
        }

        if (File.Exists(target))
        {
            if (!overwrite)
            {
                throw new ToolException("file_exists", $"'{target}' already exists.", "Pass overwrite=true to replace it.");
            }

            File.Delete(target);
        }

        string folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        DateTime started = DateTime.UtcNow;
        bool wasDirty = document.Dirty;
        try
        {
            if (flatPattern)
            {
                ModelDocument model = ModelDocument.From(document);
                model.Models.SaveAsFlatDXFEx(target, null, null, null, true);
            }
            else if (models is not null)
            {
                int type = SimplifyService.ParasolidType(target);
                if (models is [SolidEdgePart.Model single])
                {
                    single.SaveBody(target, type);
                }
                else
                {
                    ((SolidEdgePart.PartDocument)document).SaveBody(target, type);
                }
            }
            else
            {
                document.SaveCopyAs(target);
            }
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("export_failed", $"Solid Edge could not export to {format}: {ex.Message.Trim()}",
                flatPattern
                    ? "Create the flat pattern first (se_sheetmetal action 'flatten') and save the document."
                    : "Check that the document has geometry and that the translator for this format is installed.", ex);
        }

        var file = new FileInfo(target);
        if (!file.Exists || file.Length == 0)
        {
            throw new ToolException("export_failed", $"Solid Edge did not write '{target}'.",
                flatPattern ? "Create the flat pattern first (se_sheetmetal action 'flatten')." : "Check the translator options in Solid Edge.");
        }

        // STL carries no unit and Solid Edge writes it in the unit of its STL translator options (inches by default): the
        // file is scaled to mm, the unit of every tool.
        double? stlUnit = format == "STL" ? StlUnit(context, "Export Units") : null;
        StlFile.Info? stl = format != "STL" ? null : stlUnit is { } unit && unit != 1 ? StlFile.Scale(target, target, unit) : StlFile.Inspect(target);
        file.Refresh();
        // Some translators write files beside the target. Verified: OBJ writes its .mtl material file and a .log, PLM XML a
        // Parasolid file per body (named after the part) and .UUIDs.txt.
        string[] beside = [.. Directory.EnumerateFiles(folder)
            .Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTimeUtc(f) >= started)
            .Order(StringComparer.OrdinalIgnoreCase)];

        // Drawing templates fill the title block with file data (e.g. the date): if unsaved they come out
        // as "Error: No reference" (verified with the A3 template of Solid Edge 2026).
        // Verified: exporting a saved drawing to PDF marks it modified although nothing in it changed, and Solid Edge
        // refuses to clear the flag (setting Dirty gives E_FAIL).
        string? note = draft && DocumentService.PathOf(document) is null
            ? "The drawing has never been saved: title-block fields taken from its file (such as the date) show 'Error: No reference'. Save it (se_documents save_as) and export again if the title block matters."
            : !wasDirty && document.Dirty
                ? "Solid Edge marks the document as modified after this export although its content did not change: close it with saveChanges=false, or save it."
                : null;
        // PDF files are read back: pages and their sizes (mm). Verified: a model writes one page with a picture of the view, not a
        // 3D PDF (that is Publish3DPDFEx, se_publish).
        PdfFile.Info? pdf = format.EndsWith("PDF", StringComparison.Ordinal) && PdfFile.IsPdf(target) ? PdfFile.Inspect(target) : null;
        int sheets = draft && pdf is not null ? ((SolidEdgeDraft.DraftDocument)document).Sections.WorkingSection.Sheets.Count : 0;
        if (pdf is not null && !draft && !pdf.Has3D)
        {
            const string flat = "The PDF holds a picture of the view, not the 3D model: publish a 3D PDF with se_publish pdf3d.";
            note = note is null ? flat : $"{note} {flat}";
        }

        if (stl is not null && stlUnit != 1)
        {
            string stlNote = stlUnit is null
                ? "STL files carry no unit: this one is in the unit of Solid Edge's STL translator options, which could not be read."
                : $"Solid Edge writes STL in {(stlUnit == 25.4 ? "inches" : "metres")} (Export Units of its STL translator options): the server scaled the file to mm.";
            note = note is null ? stlNote : $"{note} {stlNote}";
        }

        if (pdf?.Pages < sheets)
        {
            // Verified: with the default PDF options only the active sheet is exported.
            string sheetNote = $"The PDF holds {pdf.Pages} page(s) for {sheets} working sheets: the PDF options of Solid Edge choose the sheets (se_options category 'pdf'), or export each sheet after se_drawing activate_sheet.";
            note = note is null ? sheetNote : $"{note} {sheetNote}";
        }

        return new
        {
            document = document.Name,
            path = target,
            format,
            bytes = file.Length,
            pages = pdf?.Pages,
            pageSizes = pdf?.PageSizes.Count > 0 ? pdf.PageSizes : null,
            bodies = models?.Length,
            alsoWritten = beside.Length > 0 ? beside : null,
            stl = stl is null ? null : new
            {
                triangles = stl.Triangles,
                units = stlUnit is null ? null : "mm",
                boundingBox = new
                {
                    min = stl.Min.Select(Geo.R).ToArray(),
                    max = stl.Max.Select(Geo.R).ToArray(),
                    size = stl.Max.Zip(stl.Min, (hi, lo) => Geo.R(hi - lo)).ToArray(),
                },
            },
            note,
        };
    }

    /// <summary>
    /// Batch translation of saved files, as Solid Edge Translation Services does from the command line (it opens each file
    /// in Solid Edge, saves a copy in the format and closes it). Every Solid Edge file given, or found in the folders given,
    /// that the format takes (parts, sheet metal, assemblies and weldments for model formats, drawings for drawing formats)
    /// is written into <paramref name="folder"/> with its own name; the files of a folder keep their subfolders. A file the
    /// person already has open is exported from that document, which stays open; the others are opened and closed without
    /// saving. A file that fails is reported and the batch goes on.
    /// </summary>
    public static object ExportFiles(SeContext context, string[] files, string? folder, string format, bool subfolders, bool overwrite)
    {
        string extension = "." + format.Trim().TrimStart('.').ToLowerInvariant();
        bool model = ModelFormats.ContainsKey(extension), drawing = DraftFormats.ContainsKey(extension);
        if (!model && !drawing)
        {
            throw ToolException.InvalidArgument($"'{format}' is not a format se_export writes.",
                $"Use one of: {string.Join(", ", ModelFormats.Keys.Union(DraftFormats.Keys))}.");
        }

        if (string.IsNullOrWhiteSpace(folder))
        {
            throw ToolException.InvalidArgument("A batch needs 'path': the folder the files are written to.");
        }

        string output = context.Paths.Check(folder);
        if (File.Exists(output))
        {
            throw ToolException.InvalidArgument($"'{output}' is a file: with 'files', 'path' is the output folder.", "Give a folder.");
        }

        string[] kinds = PartOnlyFormats.Contains(extension) ? [".par", ".psm"] : [.. (model ? ModelFiles : []), .. (drawing ? DraftFiles : [])];
        var jobs = new List<(string Source, string Target)>();
        foreach (string entry in files)
        {
            string input = context.Paths.Check(entry, "files");
            if (Directory.Exists(input))
            {
                foreach (string found in Directory.EnumerateFiles(input, "*", subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(f => kinds.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase))
                {
                    string relative = Path.GetRelativePath(input, Path.GetDirectoryName(found)!);
                    jobs.Add((found, Path.GetFullPath(Path.Combine(output, relative, Path.GetFileNameWithoutExtension(found) + extension))));
                }
            }
            else if (File.Exists(input))
            {
                jobs.Add((input, Path.Combine(output, Path.GetFileNameWithoutExtension(input) + extension)));
            }
            else
            {
                throw ToolException.NotFound($"'{input}' does not exist.");
            }
        }

        if (jobs.Count == 0)
        {
            throw ToolException.NotFound($"No Solid Edge files that {extension} takes ({string.Join(", ", kinds)}) were found{(subfolders ? "" : " (subfolders=true looks in subfolders too)")}.");
        }

        if (jobs.Count > MaxBatch)
        {
            throw ToolException.InvalidArgument($"{jobs.Count} files: a batch takes at most {MaxBatch}.", "Split it by folder.");
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();
        int exported = 0;
        foreach ((string source, string target) in jobs)
        {
            if (!kinds.Contains(Path.GetExtension(source), StringComparer.OrdinalIgnoreCase))
            {
                results.Add(new { file = source, error = new { code = "invalid_argument", message = $"{extension} files are written from {string.Join(", ", kinds)} files, not from this one." } });
                continue;
            }

            if (!targets.Add(target))
            {
                results.Add(new { file = source, error = new { code = "same_target", message = $"Another file of the batch is written to '{target}'." } });
                continue;
            }

            string? opened = null;
            try
            {
                string handle;
                if (DocumentService.FindOpen(context, source) is { } open)
                {
                    handle = context.DocumentHandle(open);
                }
                else
                {
                    // Closed after the export only if this call opened it: a document the person has open under another
                    // spelling of its path (a mapped drive, a junction) comes back from Documents.Open and stays open.
                    int count = context.Application.Documents.Count;
                    handle = DocumentService.Open(context, source).Handle;
                    opened = context.Application.Documents.Count > count ? handle : null;
                }

                results.Add(new { file = source, written = Export(context, handle, target, overwrite, false) });
                exported++;
            }
            catch (ToolException ex)
            {
                results.Add(new { file = source, error = new { code = ex.Code, message = ex.Message } });
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                results.Add(new { file = source, error = new { code = "export_failed", message = $"Solid Edge could not open or export it: {ex.Message.Trim()}" } });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // E.g. the previous file is open in another program and overwrite cannot delete it.
                results.Add(new { file = source, error = new { code = "export_failed", message = ex.Message.Trim() } });
            }
            finally
            {
                if (opened is not null)
                {
                    DocumentService.Close(context, opened, false);
                }
            }
        }

        return new
        {
            format = extension,
            folder = output,
            count = jobs.Count,
            exported,
            failed = jobs.Count - exported,
            files = results,
        };
    }

    /// <summary>
    /// Millimetres per unit of Solid Edge's STL translator: "Export Units" or "Import Units" of Preferences\Translators\SESTL.ini
    /// in its installation (Inch as installed; Millimeter, or Meter for import), or null when it cannot be read.
    /// </summary>
    internal static double? StlUnit(SeContext context, string key)
    {
        string? install = SolidEdgeSettings.InstallFolder(context.Application);
        string? ini = install is null ? null : Path.Combine(install, "Preferences", "Translators", "SESTL.ini");
        if (ini is null || !File.Exists(ini))
        {
            return null;
        }

        foreach (string line in File.ReadLines(ini))
        {
            string[] pair = line.Split('=', 2);
            if (pair.Length == 2 && pair[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return pair[1].Trim().ToLowerInvariant() switch { "inch" => 25.4, "millimeter" => 1, "meter" => 1000, _ => null };
            }
        }

        return null;
    }

    /// <summary>
    /// Save Selected Model: the models (design bodies) of a part chosen by their body handles. Verified in Solid Edge 2026:
    /// Model.SaveBody(file, 2 | 3) writes that body alone to a Parasolid text or binary file, and PartDocument.SaveBody(file,
    /// 2 | 3) writes every body; PartDocument.SaveBody ignores its count and models arguments (models, bodies, a single model
    /// or a version: always every body), so a subset of two or more bodies has no route. Document types 0 and 1 open a new
    /// part or sheet metal document and then fail (E_NOINTERFACE), 4 gives E_INVALIDARG.
    /// </summary>
    private static object[] ChosenModels(SeContext context, SolidEdgeDocument document, string[] bodies)
    {
        if (document is not SolidEdgePart.PartDocument)
        {
            throw new ToolException("wrong_document_type", "'bodies' writes chosen design bodies of a part.", "Export the whole document without 'bodies'.");
        }

        IReadOnlyList<(SolidEdgePart.Model Model, SolidEdgeGeometry.Body Body)> all = ModelDocument.From(document).Bodies();
        object[] chosen = [.. bodies.Select(h =>
        {
            SolidEdgeGeometry.Body body = context.Handles.Resolve<SolidEdgeGeometry.Body>(h, "bodies");
            int index = all.ToList().FindIndex(b => Late.SameObject(b.Body, body));
            return index >= 0
                ? index
                : throw ToolException.InvalidArgument($"'{h}' is not a design body of '{document.Name}'.", "Take the body handles from se_body list.");
        }).Distinct().Order().Select(i => (object)all[i].Model)];
        return chosen.Length is 1 || chosen.Length == all.Count
            ? chosen
            : throw new ToolException("not_supported", "Solid Edge writes one chosen body or every body of the part to a Parasolid file, not a few of them.",
                "Write each body to a file of its own (one call per body) or publish them as parts (se_body publish).");
    }
}
