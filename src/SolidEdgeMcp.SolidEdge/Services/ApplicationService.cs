// Copyright (c) 2026 Gabriel Spoleto Couto
// SPDX-License-Identifier: PolyForm-Noncommercial-1.0.0

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;
using IOPath = System.IO.Path;

namespace SolidEdgeMcp.SolidEdge.Services;

/// <summary>
/// Solid Edge as a program: version, edition (licence), language and installation, the add-ins of the Add-In Manager,
/// the default templates of each document type and the list of recent files.
/// </summary>
public static class ApplicationService
{
    private static readonly (string Kind, DocumentTypeConstants Type, string Extension)[] TemplateKinds =
    [
        ("part", DocumentTypeConstants.igPartDocument, ".par"),
        ("sheet_metal", DocumentTypeConstants.igSheetMetalDocument, ".psm"),
        ("assembly", DocumentTypeConstants.igAssemblyDocument, ".asm"),
        ("draft", DocumentTypeConstants.igDraftDocument, ".dft"),
        ("weldment", DocumentTypeConstants.igWeldmentDocument, ".pwd"),
        ("weldment_assembly", DocumentTypeConstants.igWeldmentAssemblyDocument, ".asm"),
    ];

    public static IReadOnlyList<string> TemplateKindNames => TemplateKinds.Select(k => k.Kind).ToList();

    public static object Info(SeContext context)
    {
        Application app = context.Application;
        object? language = OptionService.ReadApplication(context, ApplicationGlobalConstants.seApplicationGlobalDefaultUserLangID);
        return new
        {
            name = Late.TryGetString(app, "Name"),
            version = app.Version,
            edition = Late.TryGetString(app, "LicenseType"),
            language = UiService.Language(language),
            operatingSystem = Query(app, "OS"),
            installFolder = SolidEdgeSettings.InstallFolder(app),
            templateFolder = Late.TryGetString(app.Documents, "TemplatePath"),
            processId = app.ProcessID,
            documents = app.Documents.Count,
            environments = app.Environments.Count,
            addIns = app.AddIns.Count,
            note = "Commands that the licence does not include: se_command action 'check'.",
        };
    }

    public static object AddIns(SeContext context)
    {
        AddIns addIns = context.Application.AddIns;
        var items = new List<object>();
        for (int i = 1, count = addIns.Count; i <= count; i++)
        {
            items.Add(Describe(addIns.Item(i), i));
        }

        return new { count = addIns.Count, addIns = items, note = "Load or unload one with se_app action 'set_addin' (addin, connect)." };
    }

    /// <summary>Loads or unloads an add-in, as the Add-In Manager does, and returns its previous state.</summary>
    public static object SetAddIn(SeContext context, string addin, bool connect)
    {
        (AddIn target, int index) = FindAddIn(context.Application.AddIns, addin);
        bool before = target.Connect;
        try
        {
            target.Connect = connect;
        }
        catch (COMException ex) when (!ComErrors.IsGone(ex))
        {
            throw new ToolException("addin_failed", $"Solid Edge could not {(connect ? "load" : "unload")} '{target.Description}': {ex.Message}",
                "The add-in may be missing or broken; check it in the Add-In Manager of Solid Edge.", ex);
        }

        bool after = target.Connect;
        if (after != connect)
        {
            throw new ToolException("option_not_applied", $"The add-in '{target.Description}' stayed {(after ? "loaded" : "unloaded")}.",
                "Some add-ins cannot be unloaded while Solid Edge runs; use the Add-In Manager.");
        }

        return new
        {
            addIn = Describe(target, index),
            previous = before,
            connected = after,
            restore = $"To undo, call set_addin with connect = {(before ? "true" : "false")}.",
        };
    }

    public static object Templates(SeContext context)
    {
        Application app = context.Application;
        var items = TemplateKinds.Select(k =>
        {
            string? path = Template(app, k.Type);
            return new { kind = k.Kind, path, exists = path is not null && File.Exists(path) };
        }).ToList();
        return new
        {
            templates = items,
            templateFolder = Late.TryGetString(app.Documents, "TemplatePath"),
            note = "se_documents new uses these unless a template is given; change one with se_app action 'set_template'.",
        };
    }

    /// <summary>
    /// Extracts the data Solid Edge's AI automatic drawing creation learns from (the sheet, views and their placement) out of
    /// a saved drawing into a CSV file. Verified: the call opens the drawing and leaves it open and modified, so a drawing that
    /// was not open before is closed again without saving. Training the models from the CSV
    /// (CreateONNXModelsForAIAutomaticDrawingCreation) changes the installation's AI models and is not called.
    /// </summary>
    public static object AiDrawingData(SeContext context, string drawing, string? output)
    {
        string file = context.Paths.Check(drawing, "path");
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"There is no file '{file}'.");
        }

        if (!string.Equals(IOPath.GetExtension(file), ".dft", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"'{IOPath.GetFileName(file)}' is not a drawing (.dft).");
        }

        string csv = context.Paths.Check(output ?? IOPath.ChangeExtension(file, ".csv"), "output");
        if (!string.Equals(IOPath.GetExtension(csv), ".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument("'output' is a .csv file.");
        }

        Application app = context.Application;
        bool wasOpen = OpenDocument(app, file) is not null;
        DateTime before = File.Exists(csv) ? File.GetLastWriteTimeUtc(csv) : DateTime.MinValue;
        try
        {
            app.ExtractDataForAIAutomaticDrawingCreation(file, csv);
        }
        finally
        {
            if (!wasOpen && OpenDocument(app, file) is { } opened)
            {
                opened.Close(false);
            }
        }

        if (!File.Exists(csv) || File.GetLastWriteTimeUtc(csv) == before || new FileInfo(csv).Length == 0)
        {
            throw new ToolException("export_failed", $"Solid Edge wrote no data for '{IOPath.GetFileName(file)}'.", "Check that the drawing has views of a model.");
        }

        return new { drawing = file, csv, rows = File.ReadLines(csv).Count(l => l.Length > 0), bytes = new FileInfo(csv).Length };
    }

    private static SolidEdgeDocument? OpenDocument(Application app, string file) =>
        Late.Items(app.Documents).OfType<SolidEdgeDocument>()
            .FirstOrDefault(d => string.Equals(Late.TryGetString(d, "FullName"), file, StringComparison.OrdinalIgnoreCase));

    /// <summary>Sets the default template of a document type (also used by the New command of Solid Edge).</summary>
    public static object SetTemplate(SeContext context, string kind, string path)
    {
        string key = kind.Trim().ToLowerInvariant().Replace(' ', '_');
        (string Kind, DocumentTypeConstants Type, string Extension) entry = TemplateKinds.FirstOrDefault(k => k.Kind == key);
        if (entry.Kind is null)
        {
            throw ToolException.InvalidArgument($"Unknown document type '{kind}'.", $"Use one of: {string.Join(", ", TemplateKindNames)}.");
        }

        string file = DocumentService.CheckTemplate(context, path, entry.Type);
        if (!File.Exists(file))
        {
            throw ToolException.NotFound($"The template does not exist: '{file}'.");
        }

        if (!string.Equals(IOPath.GetExtension(file), entry.Extension, StringComparison.OrdinalIgnoreCase))
        {
            throw ToolException.InvalidArgument($"A {entry.Kind} template is a {entry.Extension} file.");
        }

        Application app = context.Application;
        string? before = Template(app, entry.Type);
        app.SetDefaultTemplatePath(entry.Type, file);
        string? after = Template(app, entry.Type);
        if (after is null || !string.Equals(IOPath.GetFullPath(after), file, StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("option_not_applied", $"Solid Edge kept the {entry.Kind} template at '{after}'.");
        }

        return new
        {
            kind = entry.Kind,
            previous = before,
            path = after,
            note = "Solid Edge keeps it between sessions. To undo, set the previous path again.",
        };
    }

    /// <summary>
    /// The recent files list (File menu and start screen), as Solid Edge last stored it, and how many it shows.
    /// </summary>
    public static object Recent(SeContext context)
    {
        Application app = context.Application;
        IReadOnlyList<string> files = SolidEdgeSettings.RecentFiles(app);
        return new
        {
            shown = Late.TryGet(app, "DisplayRecentFiles"),
            count = Late.TryGet(app, "DisplayRecentFilesCount"),
            files = files.Select(f => new { path = f, exists = File.Exists(f) }).ToList(),
            note = "Solid Edge stores the list as it changes. Documents opened through the API are not added; documents saved with save_as can be, even while it is suspended (suspend_recent).",
        };
    }

    /// <summary>Suspends, resumes or clears the recent files list.</summary>
    /// <summary>
    /// The file naming rule and the managed document numbering. Verified: GetNextDocumentNumbers hands out the next numbers of
    /// the numbering kept in Preferences\NumberGenerator.dat and uses them up (00000002, 00000003, then 00000004 to 00000006
    /// for three at once); the naming rule reads false on this installation.
    /// </summary>
    public static object Numbering(SeContext context, int count)
    {
        if (count is < 0 or > 100)
        {
            throw ToolException.InvalidArgument("'count' takes 0 (only read the naming rule) to 100 document numbers.");
        }

        Application app = context.Application;
        app.Get_Set_FileNamingRule(true, out bool rule);
        if (count == 0)
        {
            return new { namingRule = rule, note = "count > 0 hands out the next document numbers, which are then used up." };
        }

        int status = app.GetNextDocumentNumbers(count, out object prefix, out object numbers);
        if (status != 0)
        {
            throw new ToolException("solid_edge_error", $"Solid Edge gave no document numbers (status {status}).", "Check the numbering settings of Solid Edge.");
        }

        string[] list = numbers is Array array ? array.Cast<object>().Select(n => Convert.ToString(n, CultureInfo.InvariantCulture) ?? "").ToArray()
            : [Convert.ToString(numbers, CultureInfo.InvariantCulture) ?? ""];
        string? prefixText = prefix is Array p ? string.Join("", p.Cast<object>()) : prefix as string;
        return new { namingRule = rule, prefix = string.IsNullOrEmpty(prefixText) ? null : prefixText, numbers = list };
    }

    /// <summary>
    /// Resets the DXF/DWG (AutoCAD) translation settings of import or export to Solid Edge's defaults (ResetConfigFile).
    /// Verified: resetting import and export rewrote Preferences\Translators\seacad.ini (15916 -> 16908 bytes: the default
    /// template, paper space, layers and colour mapping written out). There is no undo. Solid Edge 2026 Foundation, which
    /// installs no seacad.ini, writes none (also when given a file path) and reports no error.
    /// </summary>
    public static object ResetDxfSettings(SeContext context, string mode)
    {
        FileTranslationMode translation = mode switch
        {
            "import" => FileTranslationMode.seImport,
            "export" => FileTranslationMode.seExport,
            _ => throw ToolException.InvalidArgument($"Unknown mode '{mode}'.", "Use import or export."),
        };
        object file = Missing.Value;
        object template = Missing.Value;
        string? install = SolidEdgeSettings.InstallFolder(context.Application);
        string? ini = install is null ? null : IOPath.Combine(install, "Preferences", "Translators", "seacad.ini");
        DateTime? before = ini is not null && File.Exists(ini) ? File.GetLastWriteTimeUtc(ini) : null;
        context.Application.ResetConfigFile(ConfigResetType.seResetAll, ConfigForForeignFileType.seAutoCADConfigFile, translation, "", ref file, ref template);
        if (ini is null)
        {
            return new { reset = "dxf_dwg_" + mode, written = (bool?)null, note = "Solid Edge reported no error, but its installation folder is unknown, so the settings file could not be checked." };
        }

        if (!File.Exists(ini))
        {
            return new { reset = "dxf_dwg_" + mode, written = (bool?)false, note = "Solid Edge wrote no settings file: this installation has no Preferences\\Translators\\seacad.ini to reset (as Solid Edge 2026 Foundation)." };
        }

        return File.GetLastWriteTimeUtc(ini) != before
            ? new { reset = "dxf_dwg_" + mode, written = (bool?)true, note = "The DXF/DWG " + mode + " settings are back to Solid Edge's defaults." }
            : new { reset = "dxf_dwg_" + mode, written = (bool?)false, note = "Solid Edge reported no error but did not rewrite Preferences\\Translators\\seacad.ini: check that it can be written." };
    }

    public static object RecentList(SeContext context, string operation)
    {
        Application app = context.Application;
        switch (operation)
        {
            case "suspend":
                app.SuspendMRU();
                return new { recent = "suspended", note = "Solid Edge stops updating the recent files until resume_recent, but documents saved with save_as are still added." };
            case "resume":
                app.ResumeMRU();
                return new { recent = "resumed" };
            case "clear":
                int before = SolidEdgeSettings.RecentFiles(app).Count;
                app.ClearMRU();
                return new { recent = "cleared", removed = before };
            default:
                throw ToolException.InvalidArgument($"Unknown operation '{operation}'.");
        }
    }

    private static object Describe(AddIn addIn, int index) => new
    {
        index,
        progId = Late.TryGetString(addIn, "ProgID"),
        guid = Late.TryGetString(addIn, "GUID"),
        description = Late.TryGetString(addIn, "Description"),
        connected = Late.TryGet(addIn, "Connect"),
        visible = Late.TryGet(addIn, "Visible"),
    };

    /// <summary>Add-in by index, ProgID, GUID or description (whole or a unique part of it).</summary>
    private static (AddIn AddIn, int Index) FindAddIn(AddIns addIns, string text)
    {
        string wanted = text.Trim();
        var all = new List<(AddIn AddIn, int Index, string Text)>();
        for (int i = 1, count = addIns.Count; i <= count; i++)
        {
            AddIn candidate = addIns.Item(i);
            string progId = Late.TryGetString(candidate, "ProgID") ?? "";
            string guid = Late.TryGetString(candidate, "GUID") ?? "";
            string description = Late.TryGetString(candidate, "Description") ?? "";
            if ((int.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) && index == i)
                || string.Equals(progId, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(guid.Trim('{', '}'), wanted.Trim('{', '}'), StringComparison.OrdinalIgnoreCase)
                || string.Equals(description, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return (candidate, i);
            }

            all.Add((candidate, i, $"{progId} {description}"));
        }

        var partial = all.Where(a => a.Text.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (partial.Count == 1)
        {
            return (partial[0].AddIn, partial[0].Index);
        }

        throw ToolException.NotFound(
            partial.Count == 0 ? $"There is no add-in '{text}'." : $"'{text}' matches several add-ins.",
            $"Add-ins: {string.Join("; ", all.Select(a => $"{a.Index} {a.Text.Trim()}"))}.");
    }

    private static string? Template(Application app, DocumentTypeConstants type)
    {
        try
        {
            string? path = app.GetDefaultTemplatePath(type);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static string? Query(Application app, string key)
    {
        try
        {
            return app.QuerySystemInformation(key) as string;
        }
        catch (COMException)
        {
            return null;
        }
    }
}
