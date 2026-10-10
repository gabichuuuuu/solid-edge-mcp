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
        [".pdf"] = "3D PDF",
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

    public static object Export(SeContext context, string? handle, string path, bool overwrite, bool flatPattern)
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
        try
        {
            if (flatPattern)
            {
                ModelDocument model = ModelDocument.From(document);
                model.Models.SaveAsFlatDXFEx(target, null, null, null, true);
            }
            else
            {
                document.SaveCopyAs(target);
            }
        }
        catch (COMException ex) when (!ComErrors.IsDisconnected(ex))
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

        // Drawing templates fill the title block with file data (e.g. the date): if unsaved they come out
        // as "Error: No reference" (verified with the A3 template of Solid Edge 2026).
        string? note = draft && DocumentService.PathOf(document) is null
            ? "The drawing has never been saved: title-block fields taken from its file (such as the date) show 'Error: No reference'. Save it (se_documents save_as) and export again if the title block matters."
            : null;
        return new { document = document.Name, path = target, format, bytes = file.Length, note };
    }
}
