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
    };

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

    public static object Export(SeContext context, string? handle, string path, bool overwrite, bool flatPattern, string[]? bodies = null)
    {
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

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
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
