using System.Reflection;
using System.Runtime.InteropServices;
using SolidEdgeFramework;
using SolidEdgeMcp.Core;
using SolidEdgeMcp.SolidEdge.Com;
using SolidEdgeMcp.SolidEdge.Session;

namespace SolidEdgeMcp.SolidEdge.Services;

public sealed record DocumentInfo(
    string Handle,
    string Name,
    string? Path,
    string Kind,
    bool Dirty,
    bool ReadOnly,
    bool Active);

/// <summary>Documents: list, create, open, save and close, always through <see cref="PathGuard"/>.</summary>
public static class DocumentService
{
    private static readonly Dictionary<string, string[]> NativeExtensions = new()
    {
        ["part"] = [".par"],
        ["sheet_metal"] = [".psm"],
        ["assembly"] = [".asm"],
        ["draft"] = [".dft"],
        ["weldment"] = [".pwd"],
        ["weldment_assembly"] = [".asm"],
    };

    /// <summary>
    /// Templates of this installation: the default one of each kind (Solid Edge's options) and the template files in its
    /// template folders, so a template is chosen from what this Solid Edge has instead of a remembered name.
    /// </summary>
    public static object Templates(SeContext context, string? kind)
    {
        string[] kinds = kind is null ? [.. DocumentKinds.CreatableKinds] : [kind];
        var defaults = new Dictionary<string, string?>();
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string k in kinds)
        {
            DocumentTypeConstants type = DocumentKinds.ForNew(k).Type;
            string? path = type == DocumentTypeConstants.igWeldmentDocument ? WeldmentAssemblyTemplate(context) ?? DefaultTemplate(context, type) : DefaultTemplate(context, type);
            defaults[k] = path is not null && File.Exists(path) ? path : null;
            foreach (string folder in TemplateFolders(context, type))
            {
                folders.Add(folder);
            }
        }

        string[] extensions = [.. kinds.SelectMany(k => NativeExtensions.GetValueOrDefault(k) ?? []).Append(".asm").Distinct()];
        string[] files = [.. folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "*.*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Take(context.MaxListItems)];
        return new
        {
            defaults,
            folders = folders.ToArray(),
            count = files.Length,
            templates = files,
            note = "Pass a template path to se_documents new ('template'); without one, the default of the kind is used.",
        };
    }

    private static string? DefaultTemplate(SeContext context, DocumentTypeConstants type)
    {
        try
        {
            return context.Application.GetDefaultTemplatePath(type);
        }
        catch (COMException)
        {
            return null;
        }
    }

    public static object List(SeContext context)
    {
        Documents documents = context.Application.Documents;
        SolidEdgeDocument? active = TryActive(context);
        var list = new List<DocumentInfo>();
        for (int i = 1; i <= documents.Count && list.Count < context.MaxListItems; i++)
        {
            if (documents.Item(i) is SolidEdgeDocument document)
            {
                list.Add(Describe(context, document, active));
            }
        }

        return new { count = documents.Count, documents = list };
    }

    public static DocumentInfo Info(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        return Describe(context, document, TryActive(context));
    }

    public static DocumentInfo Activate(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        document.Activate();
        return Describe(context, document, document);
    }

    public static DocumentInfo New(SeContext context, string kind, string? template)
    {
        (string progId, DocumentTypeConstants type) = DocumentKinds.ForNew(kind);
        object created;
        if (!string.IsNullOrWhiteSpace(template))
        {
            created = context.Application.Documents.Add(progId, CheckTemplate(context, template, type));
        }
        else if (type == DocumentTypeConstants.igWeldmentDocument && WeldmentAssemblyTemplate(context) is { } weldment)
        {
            // Verified in Solid Edge 2026: it has no .pwd template (the default template is only the folder and
            // Add fails with STG_E_PATHNOTFOUND); the weldment is an assembly created with "<units> weldment.asm".
            created = context.Application.Documents.Add("SolidEdge.AssemblyDocument", weldment);
        }
        else
        {
            created = context.Application.Documents.Add(progId);
        }
        var document = (SolidEdgeDocument)created;
        return Describe(context, document, document);
    }

    public static DocumentInfo Open(SeContext context, string path)
    {
        string checkedPath = context.Paths.Check(path);
        if (!File.Exists(checkedPath))
        {
            throw ToolException.NotFound($"The file does not exist: '{checkedPath}'.");
        }

        SolidEdgeDocument? already = FindOpen(context, checkedPath);
        if (already is not null)
        {
            already.Activate();
            return Describe(context, already, already);
        }

        var document = (SolidEdgeDocument)context.Application.Documents.Open(checkedPath);
        return Describe(context, document, document);
    }

    /// <summary>Document kind each format of other CAD systems is translated into when no kind is given.</summary>
    private static readonly Dictionary<string, string> ForeignFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        [".stp"] = "part", [".step"] = "part", [".igs"] = "part", [".iges"] = "part", [".x_t"] = "part", [".x_b"] = "part",
        [".jt"] = "part", [".sat"] = "part", [".stl"] = "part", [".obj"] = "part", [".3mf"] = "part",
        [".dxf"] = "draft", [".dwg"] = "draft",
    };

    /// <summary>
    /// Opens a file of another CAD system as a new, unsaved Solid Edge document made from a template of the given kind
    /// (Documents.OpenWithTemplate, as File > Open with a template chosen in the import options).
    /// </summary>
    public static object Import(SeContext context, string path, string? kind, string? template)
    {
        string source = context.Paths.Check(path);
        if (!File.Exists(source))
        {
            throw ToolException.NotFound($"The file does not exist: '{source}'.");
        }

        string extension = System.IO.Path.GetExtension(source);
        if (NativeExtensions.Values.Any(e => e.Contains(extension, StringComparer.OrdinalIgnoreCase)))
        {
            throw ToolException.InvalidArgument($"'{System.IO.Path.GetFileName(source)}' is already a Solid Edge document.", "Use action 'open'.");
        }

        string target = kind ?? (ForeignFormats.TryGetValue(extension, out string? byFormat) ? byFormat
            : throw ToolException.InvalidArgument($"'{extension}' is not a CAD format this server translates.",
                $"Use one of {string.Join(", ", ForeignFormats.Keys)}, or pass 'kind' to try another one."));
        (_, DocumentTypeConstants type) = DocumentKinds.ForNew(target);
        string templatePath = !string.IsNullOrWhiteSpace(template)
            ? CheckTemplate(context, template, type)
            : DefaultTemplate(context, type) is { } byDefault && File.Exists(byDefault) ? byDefault
            : throw new ToolException("template_missing", $"Solid Edge has no default {target.Replace('_', ' ')} template.",
                "Pass 'template' (se_documents templates lists them).");
        // Verified in Solid Edge 2026 with STEP, IGES, Parasolid, STL and DXF files: no dialog, a new unsaved document.
        var document = (SolidEdgeDocument)context.Application.Documents.OpenWithTemplate(source, templatePath);
        double? volume = document is SolidEdgePart.PartDocument or SolidEdgePart.SheetMetalDocument ? ModelDocument.From(document).Volume() : null;
        Dictionary<string, int>? elements = document is SolidEdgeDraft.DraftDocument draft ? DrawingService.SheetElements(draft.ActiveSheet) : null;
        if (volume is <= 0 || elements?.GetValueOrDefault("total") == 0)
        {
            TryClose(document);
            throw new ToolException("import_empty", $"Solid Edge translated no geometry from '{System.IO.Path.GetFileName(source)}', so the empty document was closed.",
                "Check the file in the program that wrote it, or pass another 'kind' or 'template'.");
        }

        return new
        {
            document = Describe(context, document, document),
            source,
            template = templatePath,
            volume = volume is { } v ? Geo.Mm3(v) : (double?)null,
            elements,
            note = "Not saved yet: save_as writes it as a Solid Edge file.",
        };
    }

    public static DocumentInfo Save(SeContext context, string? handle)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string? path = PathOf(document);
        if (path is null)
        {
            throw new ToolException("never_saved", $"'{document.Name}' has never been saved.",
                "Use action 'save_as' with a path in an allowed folder.");
        }

        context.Paths.Check(path, "document path");
        RequireWritable(document);
        document.Save();
        return Describe(context, document, TryActive(context));
    }

    public static DocumentInfo SaveAs(SeContext context, string? handle, string path, bool overwrite, bool copy)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string target = context.Paths.Check(path);
        string kind = DocumentKinds.ToKind(document.Type);
        string extension = System.IO.Path.GetExtension(target).ToLowerInvariant();
        if (NativeExtensions.TryGetValue(kind, out string[]? allowed) && !allowed.Contains(extension))
        {
            throw ToolException.InvalidArgument(
                $"A {kind.Replace('_', ' ')} document must be saved with extension {string.Join(" or ", allowed)}; got '{extension}'.",
                "To write another format (STEP, PDF, DXF, STL...) use se_export.");
        }

        if (File.Exists(target) && !overwrite)
        {
            throw new ToolException("file_exists", $"'{target}' already exists.", "Pass overwrite=true to replace it.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        if (copy)
        {
            document.SaveCopyAs(target);
        }
        else
        {
            document.SaveAs(target);
            if (document.Dirty)
            {
                // Seen with a draft and an assembly of a model that was open (not reproduced on demand): they came back
                // modified right after SaveAs and a second save left them clean, as after Save As in the interface.
                document.Save();
            }
        }

        if (!File.Exists(target))
        {
            throw new ToolException("save_failed", $"Solid Edge did not write '{target}'.");
        }

        return Describe(context, document, TryActive(context));
    }

    public static object Close(SeContext context, string? handle, bool? saveChanges)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string name = document.Name;
        bool dirty = document.Dirty;
        if (dirty && saveChanges is null)
        {
            throw new ToolException("unsaved_changes", $"'{name}' has unsaved changes.",
                "Pass saveChanges=true to save them or saveChanges=false to discard them.");
        }

        bool save = dirty && saveChanges == true;
        if (save)
        {
            string path = PathOf(document) ?? throw new ToolException("never_saved",
                $"'{name}' has never been saved.", "Use action 'save_as' first, then close.");
            context.Paths.Check(path, "document path");
        }

        string documentHandle = context.DocumentHandle(document);
        document.Close(save);
        context.Handles.InvalidateScope(documentHandle, "its document was closed");
        context.Handles.Remove(documentHandle, "the document was closed");

        // Parts loaded by an assembly or a drawing are unloaded with it: their handles are released too.
        Documents documents = context.Application.Documents;
        var open = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (int i = 1; i <= documents.Count; i++)
        {
            open.Add(documents.Item(i));
        }

        foreach ((string id, object value) in context.Handles.OfKind("doc"))
        {
            if (!open.Contains(value))
            {
                context.Handles.InvalidateScope(id, "its document was closed");
                context.Handles.Remove(id, "the document was closed");
            }
        }

        // The wrappers of the closed document's objects are released now. Left to the garbage collector, a wrapper of a
        // dead object could be handed out again for a new object that Solid Edge creates at the same address, with its old
        // interfaces (verified: an occurrence of a reopened assembly failed with RPC_E_DISCONNECTED).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Solid Edge can end up with no active document even when others are open; the last one is activated, as in the UI.
        SolidEdgeDocument? active = TryActive(context);
        if (active is null && documents.Count > 0 && documents.Item(documents.Count) is SolidEdgeDocument last)
        {
            last.Activate();
            active = last;
        }

        return new { closed = name, saved = save, active = active is null ? null : context.DocumentHandle(active) };
    }

    /// <summary>Undo, redo or recalculate (the document must support it).</summary>
    public static object Edit(SeContext context, string? handle, string action, int steps)
    {
        SolidEdgeDocument document = context.GetDocument(handle);
        string member = action switch
        {
            "undo" => "Undo",
            "redo" => "Redo",
            "recompute" => "Recompute",
            _ => throw ToolException.InvalidArgument($"Unknown edit action '{action}'."),
        };
        // Solid Edge does not record as undoable the operations created through the API from sketches, and creating a
        // sketch clears the history (verified): the number of steps is checked so that nothing that did not happen is assumed.
        string counter = action == "undo" ? "UndoTransactionCount" : "RedoTransactionCount";
        int? available = member == "Recompute" ? null : TransactionCount(document, counter);
        if (available == 0)
        {
            throw new ToolException($"nothing_to_{action}", $"Solid Edge has no step to {action} in '{document.Name}'.",
                action == "undo"
                    ? "Solid Edge does not record features made through its API from sketches, and creating a sketch clears the history: remove or suppress the feature with se_feature_edit (delete/suppress) instead."
                    : "Only steps undone with 'undo' can be redone.");
        }

        int requested = Math.Clamp(steps, 1, 100);
        int count = available is { } n ? Math.Min(requested, n) : requested;
        try
        {
            object?[] args = member == "Recompute" ? [] : [count];
            document.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, document, args, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Com.ComErrors.Unwrap(ex) is COMException { HResult: Com.ComErrors.DispEMemberNotFound or Com.ComErrors.DispEUnknownName })
        {
            throw new ToolException("not_supported", $"'{action}' is not available for a {DocumentKinds.Describe(document)}.");
        }

        // After undo or recalculate, the model objects may have changed.
        context.Handles.InvalidateScope(context.DocumentHandle(document), $"the document changed ({action})");
        DocumentInfo info = Describe(context, document, TryActive(context));
        if (available is null)
        {
            return info;
        }

        int left = TransactionCount(document, counter) ?? 0;
        if (left >= available)
        {
            throw new ToolException($"{action}_failed", $"Solid Edge did not {action} anything in '{document.Name}'.",
                "Check the document in Solid Edge; use se_feature_edit to remove or restore features.");
        }

        return new
        {
            document = info,
            steps = available - left,
            requested = count < requested ? requested : (int?)null,
            remaining = left,
            note = count < requested ? $"Only {count} step(s) were available." : null,
        };
    }

    /// <summary>Undo or redo steps that Solid Edge keeps, or null if the document does not report them.</summary>
    private static int? TransactionCount(SolidEdgeDocument document, string counter)
    {
        try
        {
            return Convert.ToInt32(document.GetType().InvokeMember(counter, BindingFlags.InvokeMethod, null, document, null,
                System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (Com.ComErrors.Unwrap(ex) is COMException or MissingMethodException)
        {
            return null;
        }
    }

    public static DocumentInfo Describe(SeContext context, SolidEdgeDocument document, SolidEdgeDocument? active)
    {
        bool readOnly = false;
        try
        {
            readOnly = document.ReadOnly;
        }
        catch (COMException)
        {
        }

        return new DocumentInfo(
            context.DocumentHandle(document),
            document.Name,
            PathOf(document),
            DocumentKinds.ToKind(document.Type),
            document.Dirty,
            readOnly,
            active is not null && ReferenceEquals(active, document));
    }

    /// <summary>Full path, or <c>null</c> if the document has never been saved.</summary>
    public static string? PathOf(SolidEdgeDocument document)
    {
        string? full = document.FullName;
        return string.IsNullOrWhiteSpace(full) || !System.IO.Path.IsPathFullyQualified(full) ? null : full;
    }

    private static SolidEdgeDocument? FindOpen(SeContext context, string path)
    {
        Documents documents = context.Application.Documents;
        for (int i = 1; i <= documents.Count; i++)
        {
            if (documents.Item(i) is SolidEdgeDocument document
                && string.Equals(PathOf(document), path, StringComparison.OrdinalIgnoreCase))
            {
                return document;
            }
        }

        return null;
    }

    /// <summary>Closes a document this call created, without saving; a failure leaves it open for the person.</summary>
    private static void TryClose(SolidEdgeDocument document)
    {
        try
        {
            document.Close(false);
        }
        catch (Exception ex) when (ComErrors.IsCallFailure(ex))
        {
            // The error that stopped the import is the one reported.
        }
    }

    private static SolidEdgeDocument? TryActive(SeContext context)
    {
        try
        {
            return context.Application.Documents.Count == 0 ? null : context.Application.ActiveDocument as SolidEdgeDocument;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static void RequireWritable(SolidEdgeDocument document)
    {
        bool readOnly;
        try
        {
            readOnly = document.ReadOnly;
        }
        catch (COMException)
        {
            return;
        }

        if (readOnly)
        {
            throw new ToolException("read_only", $"'{document.Name}' is open read-only.", "Use 'save_as' to save it under another name.");
        }
    }

    /// <summary>
    /// Templates are only read: they are accepted in the allowed folders and in the Solid Edge
    /// template folder.
    /// </summary>
    internal static string CheckTemplate(SeContext context, string template, DocumentTypeConstants type)
    {
        string full;
        try
        {
            return context.Paths.Check(template, "template");
        }
        catch (ToolException ex) when (ex.Code == "path_not_allowed")
        {
            full = System.IO.Path.GetFullPath(template);
            foreach (string folder in TemplateFolders(context, type))
            {
                if (full.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(full))
                    {
                        throw ToolException.NotFound($"The template does not exist: '{full}'.");
                    }

                    return full;
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Weldment template as an assembly when there is no installed .pwd template: the one set in Solid Edge's options
    /// (Global\Templates\WeldAsmTemplate of the running version, verified in Solid Edge 2026), otherwise one named like a
    /// weldment next to the default assembly template; null if the .pwd exists or none is found.
    /// </summary>
    private static string? WeldmentAssemblyTemplate(SeContext context)
    {
        try
        {
            if (File.Exists(context.Application.GetDefaultTemplatePath(DocumentTypeConstants.igWeldmentDocument)))
            {
                return null;
            }

            if (SolidEdgeSettings.Read(context.Application, @"Global\Templates", "WeldAsmTemplate") is { } configured && File.Exists(configured))
            {
                return configured;
            }

            string? folder = System.IO.Path.GetDirectoryName(
                context.Application.GetDefaultTemplatePath(DocumentTypeConstants.igAssemblyDocument));
            return folder is not null && Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder, "*weld*.asm").Order(StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static IEnumerable<string> TemplateFolders(SeContext context, DocumentTypeConstants type)
    {
        var folders = new List<string>();
        try
        {
            string? path = context.Application.Documents.TemplatePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                folders.Add(System.IO.Path.GetFullPath(path));
            }
        }
        catch (COMException)
        {
        }

        try
        {
            string? defaultTemplate = context.Application.GetDefaultTemplatePath(type);
            if (!string.IsNullOrWhiteSpace(defaultTemplate))
            {
                folders.Add(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(defaultTemplate))!);
            }
        }
        catch (COMException)
        {
        }

        return folders;
    }
}
