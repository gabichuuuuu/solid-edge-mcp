// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// 3D PDF publishing with the 3D PDF templates of the installation (Application.Publish3DPDFEx). Verified in Solid Edge 2026:
/// - it publishes only while no document is open, and it opens no window;
/// - within one Solid Edge session every single-file publish adds its pages to those of the previous ones (2, 4, 6…
///   pages, also with a fresh copy of the template), while publishing a folder gives each file its own pages. The server
///   therefore always publishes a folder: a single file is copied alone into a folder of the work folder (a copied
///   assembly still finds its parts where they were saved);
/// - each PDF is named after its source (a file name given to Solid Edge gets ".pdf" appended) and the PDFs of subfolders
///   land in the one output folder, so Solid Edge writes into a new empty folder and the server moves the results: it
///   never meets an existing file;
/// - STEP AP242 and JT files are embedded named after the source; the HTML option writes a self-contained .html next to
///   the PDF; ListOfAttachments embedded nothing (an array of paths, an object array, one path or paths separated by
///   semicolons, also through the typed interface);
/// - with every PMI model view selected the PDF holds a 3D view per model view (named after it) besides "Default", whatever
///   their IncludeIn3dPdfPublish flags; lists of model or named view names (strings or objects), the default model view, all
///   named views and the custom properties option changed nothing that can be found in the PDF; next/previous buttons add
///   widget annotations (80 instead of 67 with the part landscape template).
/// </summary>
public static class PublishService
{
    private static readonly string[] ModelExtensions = [".par", ".psm", ".asm", ".pwd"];

    public static object Templates(SeContext context)
    {
        List<string> templates = Installed(context);
        return new
        {
            count = templates.Count,
            templates = templates.Select(t => new { name = Path.GetFileNameWithoutExtension(t), path = t }).ToList(),
            note = templates.Count == 0
                ? "This installation has no 3D PDF templates in its template folder: give pdf3d a .dft 3D PDF template from an allowed folder."
                : null,
        };
    }

    /// <summary>Publishes a saved model, or the models of a folder, as 3D PDF files.</summary>
    public static object Pdf3d(SeContext context, string? source, string? template, string? folder, string? name, bool subfolders,
        bool attachStep, bool attachJt, bool html, bool overwrite, bool modelViews = false, bool buttons = false)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw ToolException.InvalidArgument("pdf3d requires 'source': a saved part, sheet metal or assembly file, or a folder of them.");
        }

        string input = context.Paths.Check(source, "source");
        bool isFolder = Directory.Exists(input);
        if (!isFolder && !File.Exists(input))
        {
            throw ToolException.NotFound($"'{input}' does not exist.", "Save the model first (se_documents save_as) or check the path.");
        }

        if (!isFolder && !IsModel(input))
        {
            throw ToolException.InvalidArgument($"'{Path.GetFileName(input)}' is not a part, sheet metal or assembly file ({string.Join(", ", ModelExtensions)}).",
                "Drawings are exported to PDF with se_export.");
        }

        if (isFolder && name is not null)
        {
            throw ToolException.InvalidArgument("'name' is for a single file: the PDFs of a folder are named after its files.");
        }

        List<string> models = isFolder
            ? [.. Directory.EnumerateFiles(input, "*", subfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(IsModel).Order(StringComparer.OrdinalIgnoreCase)]
            : [input];
        if (models.Count == 0)
        {
            throw ToolException.NotFound($"'{input}' has no part, sheet metal or assembly files{(subfolders ? "" : " (subfolders=true looks in its subfolders too)")}.");
        }

        if (models.GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } clash)
        {
            throw new ToolException("name_conflict", $"These files would write the same '{clash.Key}.pdf': {string.Join(", ", clash)}.",
                "Publish them one by one with different 'name' values.");
        }

        string templatePath = Template(context, template, models);
        string target = context.Paths.Check(folder ?? (isFolder ? input : Path.GetDirectoryName(input)!), "folder");
        string? baseName = name is null ? null : PdfName(name);
        var outputs = models.Select(m => (Source: m, Stem: Path.GetFileNameWithoutExtension(m),
            Pdf: Path.Combine(target, (baseName ?? Path.GetFileNameWithoutExtension(m)) + ".pdf"))).ToList();
        List<string> existing = [.. outputs.SelectMany(o => html ? new[] { o.Pdf, Path.ChangeExtension(o.Pdf, ".html") } : new[] { o.Pdf }).Where(File.Exists)];
        if (existing.Count > 0 && !overwrite)
        {
            throw new ToolException("file_exists", $"Already there: {string.Join(", ", existing.Take(5))}{(existing.Count > 5 ? $" and {existing.Count - 5} more" : "")}.",
                "Pass overwrite=true to replace them, or another 'folder' or 'name'.");
        }

        Application app = context.Application;
        int open = app.Documents.Count;
        if (open > 0)
        {
            throw new ToolException("documents_open",
                $"Solid Edge publishes 3D PDFs only while no document is open; open now ({open}): {string.Join(", ", Late.Items(app.Documents).Select(d => SeContext.SafeName((SolidEdgeDocument)d)))}.",
                "Save and close the documents you opened (se_documents close), or ask the person to close theirs, then publish again.");
        }

        string scratch = Path.Combine(context.Paths.ExchangeFolder, "publish", Guid.NewGuid().ToString("N")[..12]);
        string inbox = isFolder ? input : Path.Combine(scratch, "in");
        string outbox = Path.Combine(scratch, "out");
        try
        {
            Directory.CreateDirectory(outbox);
            if (!isFolder)
            {
                Directory.CreateDirectory(inbox);
                File.Copy(input, Path.Combine(inbox, Path.GetFileName(input)));
            }

            bool published;
            try
            {
                published = app.Publish3DPDFEx(inbox, templatePath, isFolder && subfolders, outbox, "", false, html, buttons, false,
                    modelViews, "", attachStep, attachJt);
            }
            catch (COMException ex) when (!ComErrors.IsGone(ex))
            {
                throw new ToolException("publish_failed", $"Solid Edge could not publish: {ex.Message.Trim()}",
                    "Check that the files open in Solid Edge and that the template is a 3D PDF template (action 'templates').", ex);
            }

            Directory.CreateDirectory(target);
            var wanted = new List<string>();
            if (attachStep)
            {
                wanted.Add(".step");
            }

            if (attachJt)
            {
                wanted.Add(".jt");
            }

            var files = new List<object>();
            var missing = new List<string>();
            var warnings = new List<string>();
            foreach (var output in outputs)
            {
                string written = Path.Combine(outbox, output.Stem + ".pdf");
                if (!File.Exists(written) || !PdfFile.IsPdf(written))
                {
                    missing.Add(Path.GetFileName(output.Source));
                    continue;
                }

                File.Move(written, output.Pdf, overwrite);
                string? page = null;
                if (html && File.Exists(Path.Combine(outbox, output.Stem + ".html")))
                {
                    page = Path.ChangeExtension(output.Pdf, ".html");
                    File.Move(Path.Combine(outbox, output.Stem + ".html"), page, overwrite);
                }

                PdfFile.Info info = PdfFile.Inspect(output.Pdf);
                string[] absent = [.. wanted.Where(e => !info.EmbeddedFiles.Any(f => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))];
                if (absent.Length > 0)
                {
                    warnings.Add($"{Path.GetFileName(output.Pdf)} has no embedded {string.Join(" or ", absent)} file.");
                }

                if (modelViews && info.Views3D.Count < 2)
                {
                    warnings.Add($"{Path.GetFileName(output.Pdf)} has no 3D view besides the default one: the model may have no PMI model views (se_model_view).");
                }

                if (!info.Has3D)
                {
                    warnings.Add($"{Path.GetFileName(output.Pdf)} has no 3D view: the template may not be a 3D PDF template.");
                }

                files.Add(new
                {
                    source = Path.GetFileName(output.Source),
                    path = output.Pdf,
                    bytes = new FileInfo(output.Pdf).Length,
                    pages = info.Pages,
                    has3D = info.Has3D,
                    views3D = info.Views3D.Count > 0 ? info.Views3D : null,
                    widgets = info.Widgets > 0 ? info.Widgets : (int?)null,
                    attachments = info.EmbeddedFiles.Count > 0 ? info.EmbeddedFiles : null,
                    html = page,
                });
            }

            if (files.Count == 0)
            {
                throw new ToolException("publish_failed",
                    published ? "Solid Edge reported success but wrote no PDF." : "Solid Edge published nothing (it answered false).",
                    "Check that the files open in Solid Edge and that the template is a 3D PDF template (action 'templates').");
            }

            return new
            {
                template = templatePath,
                count = files.Count,
                files,
                notPublished = missing.Count > 0 ? missing : null,
                warnings = warnings.Count > 0 ? warnings : null,
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file Solid Edge still holds stays in the work folder; the next publish uses a new folder.
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsModel(string path) => ModelExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>The .dft files of the subfolders of the template folder whose name mentions PDF ("3D PDF Publish" in Solid Edge 2026).</summary>
    private static List<string> Installed(SeContext context)
    {
        string? root = Late.TryGetString(context.Application.Documents, "TemplatePath");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        return [.. Directory.EnumerateDirectories(root)
            .Where(d => Path.GetFileName(d).Contains("PDF", StringComparison.OrdinalIgnoreCase))
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dft"))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A template by name (from <see cref="Installed"/>) or path; by default the part or assembly landscape one.</summary>
    private static string Template(SeContext context, string? template, List<string> models)
    {
        List<string> installed = Installed(context);
        if (string.IsNullOrWhiteSpace(template))
        {
            string kind = models.All(m => Path.GetExtension(m).Equals(".asm", StringComparison.OrdinalIgnoreCase)) ? "Assembly" : "Part";
            return installed.FirstOrDefault(t => Path.GetFileName(t).StartsWith(kind, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(t).Contains("Landscape", StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(t => Path.GetFileName(t).StartsWith(kind, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault()
                ?? throw new ToolException("no_template", "This installation has no 3D PDF templates in its template folder.",
                    "Pass 'template': the path of a .dft 3D PDF template in an allowed folder.");
        }

        if (PathGuard.LooksLikePath(template))
        {
            string path = DocumentService.CheckTemplate(context, template, DocumentTypeConstants.igDraftDocument, "template");
            if (!Path.GetExtension(path).Equals(".dft", StringComparison.OrdinalIgnoreCase))
            {
                throw ToolException.InvalidArgument("A 3D PDF template is a .dft file.", "List the installed ones with action 'templates'.");
            }

            return File.Exists(path) ? path : throw ToolException.NotFound($"The template does not exist: '{path}'.");
        }

        string wanted = template.EndsWith(".dft", StringComparison.OrdinalIgnoreCase) ? template[..^4] : template;
        string[] names = [.. installed.Select(Path.GetFileNameWithoutExtension).OfType<string>()];
        return NameMatch.Resolve(names, wanted) is { } found
            ? installed.First(t => Path.GetFileNameWithoutExtension(t) == found)
            : throw ToolException.NotFound($"This installation has no 3D PDF template '{template}'.{NameMatch.Hint(names, wanted)}",
                names.Length > 0 ? $"Templates: {string.Join(", ", names)}." : "Pass the path of a .dft 3D PDF template.");
    }

    private static string PdfName(string name)
    {
        string stem = name.Trim();
        if (stem.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return stem.Length == 0 || stem.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || stem.EndsWith('.')
            ? throw ToolException.InvalidArgument($"'{name}' is not a valid file name.", "Give a name such as 'bracket' (the folder goes in 'folder').")
            : stem;
    }
}
